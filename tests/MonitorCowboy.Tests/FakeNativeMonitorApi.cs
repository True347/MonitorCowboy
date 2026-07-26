using MonitorCowboy.Interop;

namespace MonitorCowboy.Tests;

/// <summary>
/// Deterministic in-memory stand-in for the Win32 layer. All members are
/// thread-safe: the worker consumer calls in from its own task.
/// </summary>
internal sealed class FakeNativeMonitorApi : INativeMonitorApi
{
    private readonly object _gate = new();
    private readonly Dictionary<byte, (uint Current, uint Max)> _values = new();
    private readonly List<(byte Code, uint Value)> _setCalls = [];
    private int _destroyed;

    public bool FailSet { get; set; }
    public bool FailGet { get; set; }

    /// <summary>When false, a successful set does not change the stored value (verify will mismatch).</summary>
    public bool ApplyWrites { get; set; } = true;

    /// <summary>Runs at the start of every set call; lets a test block the consumer deterministically.</summary>
    public Action? BeforeSet { get; set; }

    public string? Capabilities { get; set; }

    public IReadOnlyList<PhysicalMonitorHandle> MonitorsToEnumerate { get; set; } = [];

    public int DestroyedCount => Volatile.Read(ref _destroyed);

    public int LastWin32Error => 0;

    public IReadOnlyList<(byte Code, uint Value)> SetCalls
    {
        get { lock (_gate) return _setCalls.ToArray(); }
    }

    public void SetValue(byte code, uint current, uint max)
    {
        lock (_gate)
            _values[code] = (current, max);
    }

    public IReadOnlyList<PhysicalMonitorHandle> EnumerateMonitors() => MonitorsToEnumerate;

    public bool TryGetVcpFeature(nint handle, byte code, out uint currentValue, out uint maximumValue)
    {
        currentValue = 0;
        maximumValue = 0;
        if (FailGet)
            return false;

        lock (_gate)
        {
            if (!_values.TryGetValue(code, out var v))
                return false;
            currentValue = v.Current;
            maximumValue = v.Max;
            return true;
        }
    }

    public bool TrySetVcpFeature(nint handle, byte code, uint value)
    {
        BeforeSet?.Invoke();

        lock (_gate)
        {
            _setCalls.Add((code, value));
            if (FailSet)
                return false;
            if (ApplyWrites)
            {
                var max = _values.TryGetValue(code, out var v) ? v.Max : 100u;
                _values[code] = (value, max);
            }
            return true;
        }
    }

    public bool TryGetCapabilitiesString(nint handle, out string capabilities)
    {
        capabilities = Capabilities ?? "";
        return Capabilities is not null;
    }

    public void DestroyMonitor(nint handle) => Interlocked.Increment(ref _destroyed);
}
