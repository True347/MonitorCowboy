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
    private int _rebuildPending;
    private int _clearCapsPending;

    /// <summary>Raised (from worker threads) whenever any monitor's state changes. Payload is the device path.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Raised (from worker threads) when a user-initiated write ends as failed.</summary>
    public event Action<string, byte, uint>? WriteFailed;

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

    /// <summary>
    /// TTL-gated refresh of volatile values (input/volume). When
    /// <paramref name="devicePath"/> is given only that monitor is refreshed;
    /// null refreshes every ready monitor (L1 view).
    /// </summary>
    public void RequestVolatileRefresh(string? devicePath = null)
    {
        var now = DateTime.UtcNow;
        foreach (var (entry, worker) in Current())
        {
            if (devicePath is not null && entry.DevicePath != devicePath)
                continue;
            if (entry.CapsState != CapsState.Ready)
                continue;
            if (now - entry.LastValuesReadUtc < ValuesTtl)
                continue;
            if (!entry.TryBeginRefresh())
                continue;

            worker.RequestReadValues();
        }
    }

    /// <summary>False when the monitor is not currently addressable (e.g. mid-rebuild).</summary>
    public bool RequestWrite(string devicePath, byte code, uint target)
    {
        var (entry, worker) = TryFind(devicePath);
        if (entry is null)
            return false;

        worker.RequestWrite(code, target);
        return true;
    }

    public bool RequestValueReread(string devicePath)
    {
        var (entry, worker) = TryFind(devicePath);
        if (entry is null || !entry.TryBeginRefresh())
            return false;

        return worker.RequestReadValues();
    }

    public bool RequestCapsReread(string devicePath)
    {
        var (entry, worker) = TryFind(devicePath);
        if (entry is null)
            return false;

        entry.ResetCapsPending();
        if (worker.RequestReadCapabilities())
            return true;

        entry.MarkCapsReadFailed();
        return false;
    }

    /// <summary>
    /// Tear everything down and re-enumerate. Physical monitor handles must not
    /// be reused across display topology changes. Requests arriving while a
    /// rebuild is running are coalesced into one more pass by the current
    /// holder, so no topology change (or cache-clear request) is ever lost.
    /// </summary>
    public async Task RebuildTopologyAsync(bool clearCapsCache = false)
    {
        if (clearCapsCache)
            Interlocked.Exchange(ref _clearCapsPending, 1);
        Interlocked.Exchange(ref _rebuildPending, 1);

        while (await _rebuildGate.WaitAsync(0).ConfigureAwait(false))
        {
            try
            {
                while (Interlocked.Exchange(ref _rebuildPending, 0) == 1)
                {
                    var clear = Interlocked.Exchange(ref _clearCapsPending, 0) == 1;
                    if (!await RebuildOnceAsync(clear).ConfigureAwait(false))
                        return; // Lost the race against Dispose; fresh list already torn down.
                }
            }
            catch (Exception ex)
            {
                _log("Topology rebuild failed", ex);
            }
            finally
            {
                _rebuildGate.Release();
            }

            // A request that raced our exit (flag set after the last drain but
            // acquire failed before the release) would otherwise be stranded
            // with a free gate: recheck after releasing and take another turn.
            if (Volatile.Read(ref _rebuildPending) == 0)
                return;
        }

        // Someone else holds the gate; the pending flag (set above) guarantees
        // their drain loop or post-release recheck runs the requested pass.
    }

    private async Task<bool> RebuildOnceAsync(bool clearCapsCache)
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

        var fresh = BuildMonitors();

        bool published;
        lock (_gate)
        {
            published = !_disposed;
            if (published)
                _monitors = fresh;
        }

        if (!published)
        {
            await TearDownAsync(fresh).ConfigureAwait(false);
            return false;
        }

        WarmUp(fresh);
        StateChanged?.Invoke(string.Empty);
        return true;
    }

    public void Dispose()
    {
        List<(MonitorEntry Entry, DdcWorker Worker)> old;
        lock (_gate)
        {
            _disposed = true;
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
                entry.ApplyCapabilities(caps, notify: false);

            var worker = new DdcWorker(
                _api,
                entry,
                OnCapabilitiesRead,
                (code, target) => WriteFailed?.Invoke(entry.DevicePath, code, target));
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
        catch
        {
            // Timeout: a wedged DDC call can outlive the budget; the worker
            // still destroys its handle in its own finally when the call
            // returns. Any other worker fault must not abort re-enumeration.
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
