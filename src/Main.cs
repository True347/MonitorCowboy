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

    private readonly object _refreshGate = new();
    private string _lastRawQuery = "";
    private string? _lastDevicePath;
    private DateTime _lastQueryAtUtc = DateTime.MinValue;
    private DateTime _lastPushRefreshUtc = DateTime.MinValue;

    public Task InitAsync(PluginInitContext context)
    {
        _context = context;

        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;

        var settings = context.API.LoadSettingJsonStorage<PluginSettings>();
        var store = new FlowCapsStore(settings, () => context.API.SaveSettingJsonStorage<PluginSettings>());

        _service = new MonitorService(new RealNativeMonitorApi(), store, LogError);
        _service.StateChanged += OnServiceStateChanged;
        _service.Initialize();

        _factory = new ResultFactory(context.API, _service);
        _watcher = new TopologyWatcher(OnTopologyChanged);

        return Task.CompletedTask;
    }

    public Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        try
        {
            if (_service is null || _factory is null)
            {
                return Task.FromResult(new List<Result>
                {
                    new()
                    {
                        Title = "MonitorCowboy requires Windows",
                        SubTitle = "DDC/CI monitor control is only available on Windows.",
                        IcoPath = "Images/warning.png",
                        AddSelectedCount = false,
                    },
                });
            }

            var snapshots = _service.GetSnapshots();
            var intent = QueryRouter.Parse(query.Search, snapshots);

            RecordQueryContext(query.TrimmedQuery, intent);
            _service.RequestVolatileRefresh();

            return Task.FromResult(_factory.Build(intent, snapshots, query.ActionKeyword));
        }
        catch (Exception ex)
        {
            LogError("Query failed", ex);
            var message = _factory?.ErrorItem(ex.Message) ?? new Result
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

        if (_service is not null)
        {
            _service.StateChanged -= OnServiceStateChanged;
            _service.Dispose();
            _service = null;
        }
    }

    private void OnTopologyChanged()
    {
        var service = _service;
        if (service is null)
            return;

        _ = service.RebuildTopologyAsync();
    }

    private void RecordQueryContext(string rawQuery, QueryIntent intent)
    {
        var devicePath = intent switch
        {
            MonitorMenuIntent m => m.Monitor.DevicePath,
            InputMenuIntent m => m.Monitor.DevicePath,
            VolumeMenuIntent m => m.Monitor.DevicePath,
            _ => null,
        };

        lock (_refreshGate)
        {
            _lastRawQuery = rawQuery;
            _lastDevicePath = devicePath;
            _lastQueryAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Bridges background completion (warm-up, refresh, verify-after-set) to the
    /// UI: re-runs the last query so the window shows the final state. Only fires
    /// while the user is plausibly still looking (short window since the last
    /// keystroke), only for the monitor being viewed, and throttled.
    /// ChangeQuery marshals to the UI thread internally, so calling it from a
    /// worker thread is safe (ReQuery is not — do not switch to it).
    /// </summary>
    private void OnServiceStateChanged(string devicePath)
    {
        string rawQuery;
        string? contextPath;
        DateTime lastQueryAt;

        lock (_refreshGate)
        {
            rawQuery = _lastRawQuery;
            contextPath = _lastDevicePath;
            lastQueryAt = _lastQueryAtUtc;
        }

        var now = DateTime.UtcNow;
        if (rawQuery.Length == 0 || now - lastQueryAt > PushRefreshWindow)
            return;
        if (contextPath is not null && devicePath.Length > 0 && devicePath != contextPath)
            return;

        lock (_refreshGate)
        {
            if (now - _lastPushRefreshUtc < PushRefreshThrottle)
                return;
            _lastPushRefreshUtc = now;
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
