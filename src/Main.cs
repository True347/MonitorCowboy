using System.Diagnostics;
using System.Runtime.InteropServices;
using Flow.Launcher.Plugin;
using MonitorCowboy.Core;
using MonitorCowboy.Diagnostics;
using MonitorCowboy.Interop;
using MonitorCowboy.Ui;

namespace MonitorCowboy;

public class Main : IAsyncPlugin, IContextMenu, IAsyncReloadable, IDisposable
{
    private const string LogCategory = "MonitorCowboy";

    private static readonly TimeSpan PushRefreshWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PushRefreshThrottle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ToastThrottle = TimeSpan.FromSeconds(5);

    private PluginInitContext _context = null!;
    private MonitorService? _service;
    private TopologyWatcher? _watcher;
    private ResultFactory? _factory;
    private DiagSwitch? _diag;
    private Timer? _deferredRefresh;

    private readonly object _refreshGate = new();
    private readonly object _toastGate = new();
    private readonly Dictionary<(string DevicePath, byte Code), DateTime> _lastToastUtc = new();
    private string _lastRawQuery = "";
    private string _lastSeenQuery = "";
    private bool _stateDirty;
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

        var initStarted = Stopwatch.GetTimestamp();

        try
        {
            PluginSettings settings;
            var settingsSource = "ok";
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
                settingsSource = "recovered";
            }

            var save = () => context.API.SaveSettingJsonStorage<PluginSettings>();
            var store = new FlowCapsStore(settings, save);

            _diag = new DiagSwitch(
                Path.Combine(context.CurrentPluginMetadata.PluginSettingsDirectoryPath, "diagnostics"),
                BuildTraceHeader,
                enabled =>
                {
                    settings.DiagnosticsEnabled = enabled;
                    save();
                });

            // Turned on before anything else is built: a trace that starts
            // after initialization cannot describe initialization.
            if (settings.DiagnosticsEnabled)
                _diag.SetEnabled(true);

            if (_diag.Enabled)
            {
                _diag.Write("--", "INIT.START", $"settings={settingsSource} cachedCaps={settings.CapabilitiesByDevicePath.Count}");
            }

            _service = new MonitorService(new RealNativeMonitorApi(), store, LogError, trace: _diag);
            _service.StateChanged += OnServiceStateChanged;
            _service.WriteFailed += OnWriteFailed;
            _service.Initialize();

            _factory = new ResultFactory(
                context.API,
                _service,
                context.CurrentPluginMetadata.PluginDirectory,
                ClearPushRefreshContext,
                _diag,
                _diag);
            _watcher = new TopologyWatcher(OnTopologyChanged, _diag);
            _deferredRefresh = new Timer(_ => SafeDeferredRefresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            context.API.VisibilityChanged += OnVisibilityChanged;

            if (_diag.Enabled)
                _diag.Write("--", "INIT.DONE", $"ms={ElapsedMs(initStarted)}");
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

            var started = Stopwatch.GetTimestamp();
            var snapshots = service.GetSnapshots();
            var intent = QueryRouter.Parse(query.Search, snapshots);
            var devicePath = DevicePathOf(intent);

            // OriginalQuery round-trips the textbox byte-for-byte, so the push
            // refresh always hits ChangeQuery's equal-text pure-requery branch
            // (TrimmedQuery would strip the trailing space drill queries need).
            RecordQueryContext(query.OriginalQuery, devicePath, token);
            service.RequestVolatileRefresh(devicePath);

            var results = factory.Build(intent, snapshots, query.ActionKeyword);

            var diag = _diag;
            if (diag is not null && diag.Enabled)
            {
                // The elapsed time is the point of this line: the query path is
                // supposed to be a pure cache read, so anything but ~0 ms means
                // I/O has leaked into it.
                diag.Write(
                    "--",
                    "QUERY",
                    $"text={DescribeQuery(query)} intent={intent.GetType().Name} results={results.Count} monitors={snapshots.Count} ms={ElapsedMs(started)}");
            }

            return Task.FromResult(results);
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
        // Read once: disposal between the null check and the capture would
        // leave the lambdas holding a null service until the user presses Enter.
        var service = _service;
        if (selectedResult.ContextData is not string devicePath || service is null)
            return [];

        return
        [
            new Result
            {
                Title = "Refresh values",
                SubTitle = "Re-read the current input, brightness, and volume of this monitor.",
                IcoPath = "Images/monitor.png",
                AddSelectedCount = false,
                Action = _ => RunMaintenance(() => service.RequestValueReread(devicePath)),
            },
            new Result
            {
                Title = "Re-read capabilities",
                SubTitle = "Slow (can take seconds). Use after enabling DDC/CI in the monitor's OSD.",
                IcoPath = "Images/warning.png",
                AddSelectedCount = false,
                Action = _ => RunMaintenance(() => service.RequestCapsReread(devicePath)),
            },
        ];
    }

    /// <summary>
    /// Context menu actions are invoked by the host without an exception
    /// boundary of its own, so nothing may escape one — same contract the
    /// result factory enforces on the results it builds.
    /// </summary>
    private bool RunMaintenance(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogError("Maintenance action failed", ex);
        }

        return true;
    }

    public async Task ReloadDataAsync()
    {
        // Read once, and let nothing escape: this is a host-invoked entry point
        // and disposal can null the field between a check and a use.
        var service = _service;
        if (service is null)
            return;

        try
        {
            await service.RebuildTopologyAsync(clearCapsCache: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogError("Reload plugin data failed", ex);
        }
    }

    public void Dispose()
    {
        var diag = _diag;
        var traced = diag is not null && diag.Enabled;
        var step = Stopwatch.GetTimestamp();

        void Step(string name)
        {
            if (!traced)
                return;

            // Each step is expected to be sub-millisecond. A step in the
            // hundreds means something below is blocking the UI thread, which
            // is the failure this ordering exists to prevent.
            diag!.Write("--", "DISPOSE.STEP", $"step={name} ms={ElapsedMs(step)}");
            step = Stopwatch.GetTimestamp();
        }

        try
        {
            _context.API.VisibilityChanged -= OnVisibilityChanged;
        }
        catch
        {
            // Host may already be tearing the API down.
        }

        Step("visibility-unsubscribe");

        // Unsubscribe first, before anything below can block. A worker still in
        // OnServiceStateChanged must not race a disposed timer — and its push
        // refresh marshals into the UI thread synchronously, which is the very
        // thread running this method: with the subscription live, the watcher's
        // bounded wait below would deadlock against it for its full budget.
        if (_service is not null)
        {
            _service.StateChanged -= OnServiceStateChanged;
            _service.WriteFailed -= OnWriteFailed;
        }

        Step("service-unsubscribe");

        _watcher?.Dispose();
        _watcher = null;
        Step("watcher");

        _deferredRefresh?.Dispose();
        _deferredRefresh = null;
        Step("deferred-timer");

        _service?.Dispose();
        _service = null;
        Step("service");

        // Last, so every step above is already on disk.
        _diag = null;
        diag?.Dispose();
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
        BrightnessMenuIntent m => m.Monitor.DevicePath,
        _ => null,
    };

    /// <summary>
    /// Called when an action dismisses the launcher window: the push refresh
    /// must not rewrite the query text of a window the user just closed.
    /// </summary>
    private void ClearPushRefreshContext()
    {
        lock (_refreshGate)
        {
            _lastRawQuery = "";
            _lastDevicePath = null;
        }
    }

    private void RecordQueryContext(string rawQuery, string? devicePath, CancellationToken token)
    {
        lock (_refreshGate)
        {
            // A requery this plugin triggered itself must not extend the push
            // window: the window anchors to the last user-originated query.
            var selfInduced = _pushRefreshInduced && rawQuery == _lastRawQuery;
            _pushRefreshInduced = false;

            _lastRawQuery = rawQuery;
            _lastSeenQuery = rawQuery;
            _stateDirty = false; // this render reflects the current cache
            _lastDevicePath = devicePath;
            _lastQueryToken = token;
            if (!selfInduced)
                _lastQueryAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Flow's preserve-last-query mode restores the text AND the frozen result
    /// list on window show without re-running the query — so results rendered
    /// before a background write completed would display pre-write state.
    /// When the window becomes visible and monitor state changed since our
    /// last render, re-run whatever query is in the box: ours gets a fresh
    /// render, anyone else's is a harmless recompute (the text is never
    /// rewritten, so no hijacking). The event is raised on the UI thread,
    /// which is what ReQuery requires.
    /// </summary>
    private void OnVisibilityChanged(object sender, VisibilityChangedEventArgs args)
    {
        if (!args.IsVisible)
            return;

        bool dirty;
        string lastSeen;
        lock (_refreshGate)
        {
            dirty = _stateDirty;
            lastSeen = _lastSeenQuery;
            _stateDirty = false;
        }

        var diag = _diag;
        if (diag is not null && diag.Enabled)
            diag.Write("--", "REQUERY", $"on=visibility dirty={dirty} hasQuery={lastSeen.Length > 0}");

        if (!dirty || lastSeen.Length == 0)
            return;

        try
        {
            _context.API.ReQuery(false);
        }
        catch (Exception ex)
        {
            LogError("Requery on window show failed", ex);
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
        {
            // Whatever the push-refresh outcome, the frozen result list Flow
            // may restore on the next window show no longer matches the cache.
            _stateDirty = true;
            contextPath = _lastDevicePath;
        }

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

        var diag = _diag;
        var now = DateTime.UtcNow;

        // Split from the staleness check below on purpose: with no query yet
        // there is no elapsed time to report, and reporting one anyway printed
        // the span since DateTime.MinValue.
        if (rawQuery.Length == 0)
        {
            if (diag is not null && diag.Enabled)
                diag.Write("--", "PUSH", "decision=no-context");
            return;
        }

        if (now - lastQueryAt > PushRefreshWindow)
        {
            if (diag is not null && diag.Enabled)
                diag.Write("--", "PUSH", $"decision=expired since_query_ms={(long)(now - lastQueryAt).TotalMilliseconds}");
            return;
        }

        // Flow cancels a query's token when a newer keystroke supersedes it; a
        // stale refresh would overwrite text the user is currently typing.
        if (token.IsCancellationRequested)
        {
            if (diag is not null && diag.Enabled)
                diag.Write("--", "PUSH", "decision=cancelled");
            return;
        }

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

        if (diag is not null && diag.Enabled)
            diag.Write("--", "PUSH", $"decision=fire fromTimer={fromTimer} since_query_ms={(long)(now - lastQueryAt).TotalMilliseconds}");

        try
        {
            _context.API.ChangeQuery(rawQuery, true);
        }
        catch (Exception ex)
        {
            LogError("Push refresh failed", ex);
        }
    }

    /// <summary>
    /// Timer callbacks run on the thread pool, where an escaping exception is a
    /// process kill. <c>TopologyWatcher.Fire</c> carries the same guard.
    /// </summary>
    private void SafeDeferredRefresh()
    {
        try
        {
            TryPushRefresh(fromTimer: true);
        }
        catch (Exception ex)
        {
            LogError("Deferred push refresh failed", ex);
        }
    }

    /// <summary>
    /// Query text for the trace. Under its own action keyword this is the
    /// plugin's own grammar and safe to record verbatim; configured as a global
    /// keyword the plugin sees every character typed into the launcher, so only
    /// the shape of the query is recorded.
    /// </summary>
    private static string DescribeQuery(Query query)
        => string.IsNullOrEmpty(query.ActionKeyword) || query.ActionKeyword == "*"
            ? $"<global:{query.Search.Length}ch>"
            : $"\"{query.OriginalQuery}\"";

    private string BuildTraceHeader()
    {
        var meta = _context.CurrentPluginMetadata;
        return string.Join(
            Environment.NewLine,
            "== MonitorCowboy diagnostic trace ==",
            $"   plugin={meta.Version} keyword={meta.ActionKeyword} os={Environment.OSVersion.VersionString} clr={Environment.Version} arch={RuntimeInformation.ProcessArchitecture}",
            $"   opened={DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}",
            "   columns: <utc>  t=<ms since open>  <monitor>  <EVENT>  <key=value ...>");
    }

    private static long ElapsedMs(long startTimestamp)
        => (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Policy layer: how loudly each write outcome is reported, per feature.
    /// A rejected brightness write is the silent-failure shape this plugin
    /// promises to surface (the monitor confirmed it kept its value — HDR or
    /// a picture mode is likely holding it). Other rejections stay on the
    /// result badge: a slow input switch can report its old value for the
    /// whole verify window, and a toast there would cry wolf.
    /// </summary>
    private void OnWriteFailed(string devicePath, byte code, uint target, OpPhase phase)
    {
        if (phase == OpPhase.Rejected && code != Vcp.Luminance)
            return;

        if (!TryBeginToast(devicePath, code))
            return;

        var name = _service?.GetSnapshots().FirstOrDefault(s => s.DevicePath == devicePath)?.FriendlyName ?? "The monitor";
        var (title, message) = (phase, code) switch
        {
            (OpPhase.Rejected, _) => ("MonitorCowboy: brightness unchanged",
                $"{name} kept its brightness unchanged — it may be locked by HDR, dynamic contrast, or a picture mode."),
            (_, Vcp.Luminance) => ("MonitorCowboy: set failed",
                $"{name} rejected the brightness change. If HDR or a special picture mode is active, it may be refusing brightness control."),
            (_, Vcp.InputSource) => ("MonitorCowboy: set failed", $"{name} rejected the input source change."),
            _ => ("MonitorCowboy: set failed", $"{name} rejected the volume change."),
        };

        try
        {
            _context.API.ShowMsg(title, message, AbsoluteIcon("error.png"));
        }
        catch (Exception ex)
        {
            LogError("ShowMsg failed", ex);
        }
    }

    /// <summary>
    /// Repeated step presses against a locked monitor end as one rejection per
    /// executed write; one toast per monitor+feature per window is enough.
    /// </summary>
    private bool TryBeginToast(string devicePath, byte code)
    {
        var now = DateTime.UtcNow;
        lock (_toastGate)
        {
            if (_lastToastUtc.TryGetValue((devicePath, code), out var last) && now - last < ToastThrottle)
                return false;
            _lastToastUtc[(devicePath, code)] = now;
            return true;
        }
    }

    // Toast icons need absolute paths (result IcoPath is plugin-relative).
    private string AbsoluteIcon(string fileName)
        => Path.Combine(_context.CurrentPluginMetadata.PluginDirectory ?? "", "Images", fileName);

    /// <summary>
    /// Routes to the host log. An exception is a real failure and is logged as
    /// one; a bare message is a per-monitor diagnostic (capabilities outcome,
    /// probe result) and belongs at info level — logging a successful parse as
    /// a warning, on every machine, is how a shared log stops meaning anything.
    /// </summary>
    private void LogError(string message, Exception? ex)
    {
        try
        {
            if (ex is not null)
                _context.API.LogException(LogCategory, message, ex);
            else
                _context.API.LogInfo(LogCategory, message);
        }
        catch
        {
            // Logging must never take the plugin down.
        }
    }
}
