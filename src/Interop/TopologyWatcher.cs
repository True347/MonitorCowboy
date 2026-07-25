using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MonitorCowboy.Interop;

/// <summary>
/// Debounced listener for display topology changes.
///
/// Constraints honored here: the SystemEvents handler is a synchronous method
/// that only resets a timer (never does I/O — an unhandled exception on the
/// broadcast thread would take the whole host process down), and the static
/// event is unsubscribed on dispose so an uninstalled plugin cannot stay
/// rooted. Changes that produce no WM_DISPLAYCHANGE (e.g. an input switched
/// away) are covered by the value-read TTL, not by this watcher.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TopologyWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly Action _onTopologyChanged;
    private readonly Timer _timer;
    private volatile bool _disposed;

    public TopologyWatcher(Action onTopologyChanged)
    {
        _onTopologyChanged = onTopologyChanged;
        _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; nothing to do.
        }
    }

    private void Fire()
    {
        if (_disposed)
            return;

        // Timer callbacks already run on the thread pool; the callback itself
        // must still not throw.
        try
        {
            _onTopologyChanged();
        }
        catch
        {
            // The owner logs its own failures; never let one escape a timer thread.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Blocking dispose: guarantee no callback runs after this returns, so
        // the owner can tear down what the callback would have touched.
        using var done = new ManualResetEvent(false);
        if (_timer.Dispose(done))
            done.WaitOne(TimeSpan.FromSeconds(1));
    }
}
