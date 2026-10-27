using MonitorCowboy.Core;

namespace MonitorCowboy;

/// <summary>
/// Capabilities cache and per-monitor preferences, both backed by Flow's
/// plugin JSON storage. One store with one lock on purpose: every save
/// serializes the whole settings object, so a write to either half must not
/// race the other half's enumeration.
/// </summary>
public sealed class FlowSettingsStore : ICapsStore, IMonitorPrefs
{
    private readonly PluginSettings _settings;
    private readonly Action _save;
    private readonly object _gate = new();

    public FlowSettingsStore(PluginSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    public string? TryGet(string devicePath)
    {
        lock (_gate)
            return _settings.CapabilitiesByDevicePath.TryGetValue(devicePath, out var raw) ? raw : null;
    }

    public void Put(string devicePath, string rawCapabilities)
    {
        // Save() must run under the lock: Flow serializes the dictionary, and
        // a concurrent Put from another monitor's worker would race the
        // enumeration.
        lock (_gate)
        {
            _settings.CapabilitiesByDevicePath[devicePath] = rawCapabilities;
            Save();
        }
    }

    /// <summary>Clears the capabilities cache only; preferences are the user's, not derived state.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _settings.CapabilitiesByDevicePath.Clear();
            Save();
        }
    }

    public bool IsBrightnessWriteOnly(string devicePath)
    {
        lock (_gate)
            return _settings.BrightnessWriteOnlyDevicePaths.Contains(devicePath);
    }

    public void SetBrightnessWriteOnly(string devicePath, bool writeOnly)
    {
        lock (_gate)
        {
            var changed = writeOnly
                ? _settings.BrightnessWriteOnlyDevicePaths.Add(devicePath)
                : _settings.BrightnessWriteOnlyDevicePaths.Remove(devicePath);
            if (changed)
                Save();
        }
    }

    private void Save()
    {
        try
        {
            _save();
        }
        catch
        {
            // Persistence is best-effort; a failed save only costs a future
            // capabilities re-read, or marking a monitor write-only again.
        }
    }
}
