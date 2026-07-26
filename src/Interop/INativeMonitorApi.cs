namespace MonitorCowboy.Interop;

/// <summary>
/// Stable way to re-locate one physical monitor: the GDI adapter name of its
/// HMONITOR plus its index in that HMONITOR's physical monitor array. Handles
/// themselves are deliberately never held long-term — they are acquired per
/// operation and destroyed immediately (field diagnosis: handles acquired at
/// startup can go stale on some driver stacks, after which every I2C exchange
/// through them fails with 0xC0262582 while fresh handles work).
/// </summary>
public readonly record struct MonitorRef(string GdiDeviceName, int Index);

/// <summary>One enumerated physical monitor with resolved identity.</summary>
public sealed record PhysicalMonitorInfo(MonitorRef Ref, string DevicePath, string FriendlyName, bool IsInternal);

/// <summary>
/// Thin seam over the Win32 monitor APIs so the layers above can be tested
/// offline with a fake. Every DDC/CI call blocks for the duration of the
/// exchange and must run on background workers, never on the query path.
/// Implementations must never throw.
/// </summary>
public interface INativeMonitorApi
{
    /// <summary>
    /// Enumerates all physical monitors with their resolved identity.
    /// Never throws; returns an empty list on failure. Acquires no handles.
    /// </summary>
    IReadOnlyList<PhysicalMonitorInfo> EnumerateMonitors();

    /// <summary>Reads the current and maximum value of a VCP feature (~40 ms).</summary>
    bool TryGetVcpFeature(MonitorRef monitor, byte code, out uint currentValue, out uint maximumValue);

    /// <summary>
    /// Writes a VCP feature value (~50 ms). A true result is not proof of
    /// success; callers verify by reading the value back.
    /// </summary>
    bool TrySetVcpFeature(MonitorRef monitor, byte code, uint value);

    /// <summary>
    /// Reads the raw capabilities string (usually fast, sometimes seconds).
    /// Returns false on failure.
    /// </summary>
    bool TryGetCapabilitiesString(MonitorRef monitor, out string capabilities);

    /// <summary>Win32 error code captured by the most recent failed Try* call; 0 when unavailable.</summary>
    int LastWin32Error { get; }
}
