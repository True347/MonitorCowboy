namespace MonitorCowboy.Interop;

/// <summary>
/// One enumerated physical monitor. <see cref="DevicePath"/> is the display
/// config target device path used as the primary identity/cache key;
/// <see cref="IsInternal"/> marks built-in panels (no DDC/CI). The receiver
/// owns <see cref="Handle"/> and must release it via
/// <see cref="INativeMonitorApi.DestroyMonitor"/>.
/// </summary>
public sealed record PhysicalMonitorHandle(nint Handle, string DevicePath, string FriendlyName, bool IsInternal);

/// <summary>
/// Thin seam over the Win32 monitor APIs so the layers above can be tested
/// offline with a fake. Every DDC/CI call blocks for the duration of the
/// exchange and must run on background workers, never on the query path.
/// </summary>
public interface INativeMonitorApi
{
    /// <summary>
    /// Enumerates all physical monitors with their resolved identity.
    /// Never throws; returns an empty list on failure.
    /// </summary>
    IReadOnlyList<PhysicalMonitorHandle> EnumerateMonitors();

    /// <summary>Reads the current and maximum value of a VCP feature (~40 ms).</summary>
    bool TryGetVcpFeature(nint handle, byte code, out uint currentValue, out uint maximumValue);

    /// <summary>
    /// Writes a VCP feature value (~50 ms). A true result is not proof of
    /// success; callers verify by reading the value back.
    /// </summary>
    bool TrySetVcpFeature(nint handle, byte code, uint value);

    /// <summary>
    /// Reads the raw capabilities string (usually fast, sometimes seconds).
    /// Returns false on failure.
    /// </summary>
    bool TryGetCapabilitiesString(nint handle, out string capabilities);

    /// <summary>Releases a handle obtained from <see cref="EnumerateMonitors"/>.</summary>
    void DestroyMonitor(nint handle);

    /// <summary>Win32 error code captured by the most recent failed Try* call; 0 when unavailable.</summary>
    int LastWin32Error { get; }
}
