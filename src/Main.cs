using Flow.Launcher.Plugin;
using MonitorCowboy.Core;
using MonitorCowboy.Interop;
using MonitorCowboy.Ui;

namespace MonitorCowboy;

public class Main : IAsyncPlugin, IContextMenu, IAsyncReloadable, IDisposable
{
    private static readonly TimeSpan PushRefreshWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PushRefreshThrottle = TimeSpan.FromMilliseconds(300);

    private PluginInitContext _context = null!;
    private MonitorService? _service;
    private TopologyWatcher? _watcher;
    private ResultFactory? _factory;
    private Timer? _deferredRefresh;

    private readonly object _refreshGate = new();
    private string _lastRawQuery = "";
    private string? _lastDevicePath;
    private CancellationToken _lastQueryToken = CancellationToken.None;
    private DateTime _lastQueryAtUtc = DateTime.MinValue;
    private DateTime _lastPushRefreshUtc = DateTime.MinValue;
    private bool _pushRefreshInduced;
    private bool _initFailed;

    public Task InitAsync(PluginInitContext context)
    {
        _context = context;

        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;

        try
        {
            PluginSettings settings;
            try
            {
                settings = context.API.LoadSettingJsonStorage<PluginSettings>();
            }
            catch (Exception ex)
            {
                // A corrupt settings file must not brick the plugin; the caps
                // cache is best-effort by design.
                LogError("Settings load failed; starting with an empty caps cache", ex);
                settings = new PluginSettings();
            }

            var store = new FlowCapsStore(settings, () => context.API.SaveSettingJsonStorage<PluginSettings>());

            _service = new MonitorService(new RealNativeMonitorApi(), store, LogError);
            _service.StateChanged += OnServiceStateChanged;
            _service.WriteFailed += OnWriteFailed;
            _service.Initialize();

            _factory = new ResultFactory(context.API, _service, context.CurrentPluginMetadata.PluginDirectory);
            _watcher = new TopologyWatcher(OnTopologyChanged);
            _deferredRefresh = new Timer(_ => TryPushRefresh(fromTimer: true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            LogError("Initialization failed", ex);
            _initFailed = true;

            // Tear down whatever was partially built so no workers or monitor
            // handles outlive a failed init.
            try
            {
                Dispose();
            }
            catch (Exception disposeEx)
            {
                LogError("Cleanup after failed initialization", disposeEx);
            }
            _factory = null;
        }

        return Task.CompletedTask;
    }

    public Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        var service = _service;
        var factory = _factory;

        try
        {
            if (service is null || factory is null)
            {
                return Task.FromResult(new List<Result>
                {
                    new()
                    {
                        Title = _initFailed ? "MonitorCowboy failed to initialize" : "MonitorCowboy requires Windows",
                        SubTitle = _initFailed
                            ? "See the Flow Launcher log for details, then try Reload Plugin Data."
                            : "DDC/CI monitor control is only available on Windows.",
                        IcoPath = _initFailed ? "Images/error.png" : "Images/warning.png",
                        AddSelectedCount = false,
                    },
                });
            }

            var snapshots = service.GetSnapshots();
            var intent = QueryRouter.Parse(query.Search, snapshots);
            var devicePath = DevicePathOf(intent);

            // OriginalQuery round-trips the textbox byte-for-byte, so the push
            // refresh always hits ChangeQuery's equal-text pure-requery branch
            // (TrimmedQuery would strip the trailing space drill queries need).
            RecordQueryContext(query.OriginalQuery, devicePath, token);
            service.RequestVolatileRefresh(devicePath);

            return Task.FromResult(factory.Build(intent, snapshots, query.ActionKeyword));
        }
        catch (Exception ex)
        {
            LogError("Query failed", ex);
            var message = factory?.ErrorItem(ex.Message, ResultFactory.PrefixFor(query.ActionKeyword)) ?? new Result
            {
                Title = "MonitorCowboy error",
                SubTitle = ex.Message,
                IcoPath = "Images/error.png",
                AddSelectedCount = false,
            };
            return Task.FromResult(new List<Result> { message });
        }
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not string devicePath || _service is null)
            return [];

        var service = _service;
        return
        [
            new Result
            {
                Title = "Refresh values",
                SubTitle = "Re-read the current input and volume of this monitor.",
                IcoPath = "Images/monitor.png",
                AddSelectedCount = false,
                Action = _ => { service.RequestValueReread(devicePath); return true; },
            },
            new Result
            {
                Title = "Re-read capabilities",
                SubTitle = "Slow (can take seconds). Use after enabling DDC/CI in the monitor's OSD.",
                IcoPath = "Images/warning.png",
                AddSelectedCount = false,
                Action = _ => { service.RequestCapsReread(devicePath); return true; },
            },
        ];
    }

    public async Task ReloadDataAsync()
    {
        if (_service is not null)
            await _service.RebuildTopologyAsync(clearCapsCache: true).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;

        // Unsubscribe before touching the timer: a worker still in
        // OnServiceStateChanged must not race a disposed timer.
        if (_service is not null)
        {
            _service.StateChanged -= OnServiceStateChanged;
            _service.WriteFailed -= OnWriteFailed;
        }

        _deferredRefresh?.Dispose();
        _deferredRefresh = null;

        _service?.Dispose();
        _service = null;
    }

    private void OnTopologyChanged()
    {
        var service = _service;
        if (service is null)
            return;

        _ = service.RebuildTopologyAsync();
    }

    private static string? DevicePathOf(QueryIntent intent) => intent switch
    {
        MonitorMenuIntent m => m.Monitor.DevicePath,
        InputMenuIntent m => m.Monitor.DevicePath,
        VolumeMenuIntent m => m.Monitor.DevicePath,
        _ => null,
    };

    private void RecordQueryContext(string rawQuery, string? devicePath, CancellationToken token)
    {
        lock (_refreshGate)
        {
            // A requery this plugin triggered itself must not extend the push
            // window: the window anchors to the last user-originated query.
            var selfInduced = _pushRefreshInduced && rawQuery == _lastRawQuery;
            _pushRefreshInduced = false;

            _lastRawQuery = rawQuery;
            _lastDevicePath = devicePath;
            _lastQueryToken = token;
            if (!selfInduced)
                _lastQueryAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Bridges background completion (warm-up, refresh, verify-after-set) to
    /// the UI by re-running the last query. ChangeQuery marshals to the UI
    /// thread internally and its equal-text + requery branch re-runs the query
    /// without touching the textbox (ReQuery is not thread-safe — keep away).
    /// </summary>
    private void OnServiceStateChanged(string devicePath)
    {
        string? contextPath;
        lock (_refreshGate)
            contextPath = _lastDevicePath;

        if (contextPath is not null && devicePath.Length > 0 && devicePath != contextPath)
            return;

        TryPushRefresh(fromTimer: false);
    }

    private void TryPushRefresh(bool fromTimer)
    {
        string rawQuery;
        CancellationToken token;
        DateTime lastQueryAt;
        lock (_refreshGate)
        {
            rawQuery = _lastRawQuery;
            token = _lastQueryToken;
            lastQueryAt = _lastQueryAtUtc;
        }

        var now = DateTime.UtcNow;
        if (rawQuery.Length == 0 || now - lastQueryAt > PushRefreshWindow)
            return;
        // Flow cancels a query's token when a newer keystroke supersedes it; a
        // stale refresh would overwrite text the user is currently typing.
        if (token.IsCancellationRequested)
            return;

        lock (_refreshGate)
        {
            var sinceLast = now - _lastPushRefreshUtc;
            if (sinceLast < PushRefreshThrottle)
            {
                // Trailing edge: never drop the final state transition (e.g.
                // Applying… -> ✓); re-fire once the throttle window has passed.
                if (!fromTimer)
                {
                    try
                    {
                        _deferredRefresh?.Change(PushRefreshThrottle - sinceLast, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Raced plugin disposal; nothing left to refresh.
                    }
                }
                return;
            }

            _lastPushRefreshUtc = now;
            _pushRefreshInduced = true;
        }

        try
        {
            _context.API.ChangeQuery(rawQuery, true);
        }
        catch (Exception ex)
        {
            LogError("Push refresh failed", ex);
        }
    }

    private void OnWriteFailed(string devicePath, byte code, uint target)
    {
        var feature = code == Vcp.InputSource ? "input source" : "volume";
        var name = _service?.GetSnapshots().FirstOrDefault(s => s.DevicePath == devicePath)?.FriendlyName ?? "The monitor";

        try
        {
            _context.API.ShowMsg(
                "MonitorCowboy: set failed",
                $"{name} rejected the {feature} change.",
                AbsoluteIcon("error.png"));
        }
        catch (Exception ex)
        {
            LogError("ShowMsg failed", ex);
        }
    }

    // Toast icons need absolute paths (result IcoPath is plugin-relative).
    private string AbsoluteIcon(string fileName)
        => Path.Combine(_context.CurrentPluginMetadata.PluginDirectory ?? "", "Images", fileName);

    private void LogError(string message, Exception? ex)
    {
        try
        {
            if (ex is not null)
                _context.API.LogException(nameof(Main), message, ex);
            else
                _context.API.LogWarn(nameof(Main), message);
        }
        catch
        {
            // Logging must never take the plugin down.
        }
    }
}
