namespace MonitorCowboy;

/// <summary>
/// Persisted via Flow's plugin JSON storage, which lives under the Settings
/// folder and therefore survives plugin updates (the install folder does not).
/// </summary>
public class PluginSettings
{
    public Dictionary<string, string> CapabilitiesByDevicePath { get; set; } = new();
}
