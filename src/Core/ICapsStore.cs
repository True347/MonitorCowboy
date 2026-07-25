namespace MonitorCowboy.Core;

/// <summary>
/// Persistent store for raw capabilities strings, keyed by monitor device path.
/// Capability reads can take seconds, so results survive plugin restarts and
/// updates. Implementations must never throw.
/// </summary>
public interface ICapsStore
{
    string? TryGet(string devicePath);

    void Put(string devicePath, string rawCapabilities);

    void Clear();
}
