using MonitorCowboy.Interop;

namespace MonitorCowboy.Core;

/// <summary>
/// Long-lived owner of all monitor state. The query path only ever calls
/// <see cref="GetSnapshots"/> (pure cache read); every DDC/CI operation runs on
/// the per-monitor <see cref="DdcWorker"/>s in the background.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private static readonly TimeSpan ValuesTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(2);

    private readonly INativeMonitorApi _api;
    private readonly ICapsStore _capsStore;
    private readonly Action<string, Exception?> _log;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly object _gate = new();

    private List<(MonitorEntry Entry, DdcWorker Worker)> _monitors = [];
    private bool _disposed;

    /// <summary>Raised (from worker threads) whenever any monitor's state changes. Payload is the device path.</summary>
    public event Action<string>? StateChanged;

    public MonitorService(INativeMonitorApi api, ICapsStore capsStore, Action<string, Exception?> log)
    {
        _api = api;
        _capsStore = capsStore;
        _log = log;
    }

    /// <summary>Enumerate monitors and start workers. Cheap; capability reads happen in the background.</summary>
    public void Initialize()
    {
        var fresh = BuildMonitors();
        lock (_gate)
            _monitors = fresh;

        WarmUp(fresh);
    }

    public IReadOnlyList<MonitorSnapshot> GetSnapshots()
    {
        List<(MonitorEntry Entry, DdcWorker Worker)> monitors;
        lock (_gate)
            monitors = _monitors;

        return monitors.Select(m => m.Entry.BuildSnapshot()).ToList();
    }

    /// <summary>TTL-gated refresh of volatile values (input/volume) for every ready monitor.</summary>
    public void RequestVolatileRefresh()
    {
        var now = DateTime.UtcNow;
        foreach (var (entry, worker) in Current())
        {
            if (entry.CapsState != CapsState.Ready)
                continue;
            if (now - entry.LastValuesReadUtc < ValuesTtl)
                continue;
            if (!entry.TryBeginRefresh())
                continue;

            worker.RequestReadValues();
        }
    }

    public void RequestWrite(string devicePath, byte code, uint target)
    {
        if (TryFind(devicePath) is var (entry, worker) && entry is not null)
            worker.RequestWrite(code, target);
    }

    public void RequestValueReread(string devicePath)
    {
        if (TryFind(devicePath) is var (entry, worker) && entry is not null && entry.TryBeginRefresh())
            worker.RequestReadValues();
    }

    public void RequestCapsReread(string devicePath)
    {
        if (TryFind(devicePath) is var (entry, worker) && entry is not null)
        {
            entry.ResetCapsPending();
            worker.RequestReadCapabilities();
        }
    }

    /// <summary>
    /// Tear everything down and re-enumerate. Physical monitor handles must not
    /// be reused across display topology changes.
    /// </summary>
    public async Task RebuildTopologyAsync(bool clearCapsCache = false)
    {
        if (!await _rebuildGate.WaitAsync(0).ConfigureAwait(false))
            return; // A rebuild is already running; the debounced watcher will fire again if needed.

        try
        {
            List<(MonitorEntry Entry, DdcWorker Worker)> old;
            lock (_gate)
            {
                old = _monitors;
                _monitors = [];
            }

            await TearDownAsync(old).ConfigureAwait(false);

            if (clearCapsCache)
                _capsStore.Clear();

            if (_disposed)
                return;

            var fresh = BuildMonitors();
            lock (_gate)
                _monitors = fresh;

            WarmUp(fresh);
            StateChanged?.Invoke(string.Empty);
        }
        catch (Exception ex)
        {
            _log("Topology rebuild failed", ex);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;

        List<(MonitorEntry Entry, DdcWorker Worker)> old;
        lock (_gate)
        {
            old = _monitors;
            _monitors = [];
        }

        // Bounded synchronous wait: never hold up the host's shutdown.
        try
        {
            TearDownAsync(old).Wait(TeardownBudget);
        }
        catch (Exception ex)
        {
            _log("Dispose teardown", ex);
        }
    }

    private List<(MonitorEntry, DdcWorker)> BuildMonitors()
    {
        var result = new List<(MonitorEntry, DdcWorker)>();
        var index = 0;

        foreach (var handle in _api.EnumerateMonitors())
        {
            if (handle.IsInternal)
            {
                // Internal panels do not speak DDC/CI; release the handle instead of leaking it.
                _api.DestroyMonitor(handle.Handle);
                continue;
            }

            index++;
            var entry = new MonitorEntry(index, handle.Handle, handle.DevicePath, handle.FriendlyName, OnEntryChanged);

            var cachedRaw = _capsStore.TryGet(handle.DevicePath);
            if (cachedRaw is not null && CapabilitiesParser.Parse(cachedRaw) is { } caps)
                entry.ApplyCapabilities(caps);

            var worker = new DdcWorker(_api, entry, OnCapabilitiesRead);
            result.Add((entry, worker));
        }

        return result;
    }

    private void WarmUp(List<(MonitorEntry Entry, DdcWorker Worker)> monitors)
    {
        foreach (var (entry, worker) in monitors)
        {
            if (entry.CapsState == CapsState.Pending)
            {
                worker.RequestReadCapabilities(); // Chains a value read on completion.
            }
            else if (entry.TryBeginRefresh())
            {
                worker.RequestReadValues();
            }
        }
    }

    private static async Task TearDownAsync(List<(MonitorEntry Entry, DdcWorker Worker)> monitors)
    {
        foreach (var (_, worker) in monitors)
            worker.Complete();

        var completions = monitors.Select(m => m.Worker.Completion).ToArray();
        try
        {
            await Task.WhenAll(completions).WaitAsync(TeardownBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A wedged DDC call can outlive the budget; workers still destroy
            // their handle in their own finally when the call returns.
        }
    }

    private (MonitorEntry? Entry, DdcWorker Worker) TryFind(string devicePath)
    {
        foreach (var (entry, worker) in Current())
        {
            if (entry.DevicePath == devicePath)
                return (entry, worker);
        }

        return (null, null!);
    }

    private List<(MonitorEntry Entry, DdcWorker Worker)> Current()
    {
        lock (_gate)
            return _monitors;
    }

    private void OnEntryChanged(MonitorEntry entry) => StateChanged?.Invoke(entry.DevicePath);

    private void OnCapabilitiesRead(string devicePath, string raw)
    {
        try
        {
            _capsStore.Put(devicePath, raw);
        }
        catch (Exception ex)
        {
            _log("Persisting capabilities cache", ex);
        }
    }
}
