namespace MonitorCowboy;

/// <summary>
/// Persisted via Flow's plugin JSON storage, which lives under the Settings
/// folder and therefore survives plugin updates (the install folder does not).
/// </summary>
public class PluginSettings
{
    private Dictionary<string, string> _capabilitiesByDevicePath = new();
    private HashSet<string> _brightnessWriteOnlyDevicePaths = new();

    /// <summary>
    /// Whether the structured diagnostic trace is running. Off by default and
    /// persisted here so that a support session survives a host restart, which
    /// several failure modes can only be reproduced across.
    /// </summary>
    public bool DiagnosticsEnabled { get; set; }

    /// <summary>
    /// The setter coalesces null away: Flow only guards against a null root
    /// object, so a hand-edited or partially written file carrying an explicit
    /// null here would deserialize into a valid settings object with no map,
    /// and the first cache read would brick startup for good.
    /// </summary>
    public Dictionary<string, string> CapabilitiesByDevicePath
    {
        get => _capabilitiesByDevicePath;
        set => _capabilitiesByDevicePath = value ?? new();
    }

    /// <summary>
    /// Monitors the user marked as applying brightness writes without ever
    /// reporting them back. Keyed by device path like the capabilities cache,
    /// but a choice rather than a cache: clearing the cache keeps it. Null is
    /// coalesced away for the same reason as above.
    /// </summary>
    public HashSet<string> BrightnessWriteOnlyDevicePaths
    {
        get => _brightnessWriteOnlyDevicePaths;
        set => _brightnessWriteOnlyDevicePaths = value ?? new();
    }
}
