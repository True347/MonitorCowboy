namespace MonitorCowboy.Core;

/// <summary>
/// Per-monitor choices the user made, keyed by device path like the
/// capabilities cache. Unlike the cache they are settings, not derived state,
/// so clearing the cache leaves them alone. Implementations must never throw.
/// </summary>
public interface IMonitorPrefs
{
    bool IsBrightnessWriteOnly(string devicePath);

    void SetBrightnessWriteOnly(string devicePath, bool writeOnly);
}
