using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using MonitorCowboy.Core;

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
    private readonly IDiag _trace;
    private readonly Timer _timer;
    private volatile bool _disposed;

    public TopologyWatcher(Action onTopologyChanged, IDiag? trace = null)
    {
        _onTopologyChanged = onTopologyChanged;
        _trace = trace ?? NullDiag.Instance;
        _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_trace.Enabled)
                _trace.Write("--", "TOPOLOGY.EVENT", $"source=DisplaySettingsChanged debounce_ms={(long)Debounce.TotalMilliseconds}");

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
        //
        // The handle is deliberately never disposed. The runtime signals it
        // when the last timer callback finishes, which can be after this
        // bounded wait gives up (a callback that reaches ChangeQuery blocks on
        // the UI thread this very method is running on). Closing it first
        // would make that signal throw ObjectDisposedException on a thread
        // pool thread, with nothing to catch it — taking the host down. One
        // leaked event handle per plugin lifetime is the cheaper trade.
        var started = Stopwatch.GetTimestamp();
        var done = new ManualResetEvent(false);
        var removed = _timer.Dispose(done);
        var signaled = !removed || done.WaitOne(TimeSpan.FromSeconds(1));

        if (_trace.Enabled)
        {
            // wait=timeout here means a callback outlived the budget — the
            // exact condition the undisposed handle above exists to survive.
            _trace.Write(
                "--",
                "DISPOSE.WATCHER",
                $"removed={removed} wait={(signaled ? "signaled" : "timeout")} ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        }
    }
}
