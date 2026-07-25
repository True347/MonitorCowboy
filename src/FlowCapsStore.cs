using MonitorCowboy.Core;

namespace MonitorCowboy;

/// <summary>Capabilities cache backed by Flow's plugin JSON storage.</summary>
public sealed class FlowCapsStore : ICapsStore
{
    private readonly PluginSettings _settings;
    private readonly Action _save;
    private readonly object _gate = new();

    public FlowCapsStore(PluginSettings settings, Action save)
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
        lock (_gate)
            _settings.CapabilitiesByDevicePath[devicePath] = rawCapabilities;
        Save();
    }

    public void Clear()
    {
        lock (_gate)
            _settings.CapabilitiesByDevicePath.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            _save();
        }
        catch
        {
            // Cache persistence is best-effort; a failed save only costs a
            // future capabilities re-read.
        }
    }
}
