namespace MonitorCowboy;

/// <summary>
/// Persisted via Flow's plugin JSON storage, which lives under the Settings
/// folder and therefore survives plugin updates (the install folder does not).
/// </summary>
public class PluginSettings
{
    private Dictionary<string, string> _capabilitiesByDevicePath = new();

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
}
