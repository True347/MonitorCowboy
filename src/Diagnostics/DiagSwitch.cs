using MonitorCowboy.Core;

namespace MonitorCowboy.Diagnostics;

/// <summary>
/// The stable <see cref="IDiag"/> every component captures at construction, so
/// the trace can be switched on and off mid-session without rebuilding the
/// monitor list or restarting the host. Turning it on opens a fresh file;
/// turning it off closes the current one.
/// </summary>
public sealed class DiagSwitch : IDiag, IDiagnosticsControl, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string> _header;
    private readonly Action<bool> _persist;
    private readonly long _maxFileBytes;

    private volatile DiagLog? _log;
    private bool _disposed;

    /// <param name="directoryPath">Folder the traces live in.</param>
    /// <param name="header">Builds the session header, evaluated when a trace opens.</param>
    /// <param name="persist">Records the new state so it survives a host restart.</param>
    /// <param name="maxFileBytes">Size cap per trace; lowered by tests to reach the cap quickly.</param>
    public DiagSwitch(
        string directoryPath,
        Func<string> header,
        Action<bool> persist,
        long maxFileBytes = DiagLog.DefaultMaxFileBytes)
    {
        DirectoryPath = directoryPath;
        _header = header;
        _persist = persist;
        _maxFileBytes = maxFileBytes;
    }

    public string DirectoryPath { get; }

    public bool Enabled => _log?.Enabled ?? false;

    public string? FilePath => _log?.FilePath;

    public long Written => _log?.Written ?? 0;

    public long Dropped => _log?.Dropped ?? 0;

    public void Write(string tag, string code, string fields) => _log?.Write(tag, code, fields);

    public void SetEnabled(bool enabled)
    {
        DiagLog? closing = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (enabled)
            {
                // Only a *running* trace means there is nothing to do. A log
                // that retired itself — size cap reached, or an I/O fault —
                // still occupies the field while reporting itself off, and
                // treating that as "already on" would strand the support
                // surface in exactly the state it exists to diagnose: no
                // reachable action turns it back on, only a host restart.
                if (_log is { Enabled: true })
                    return;

                closing = _log;
                _log = null;

                try
                {
                    _log = new DiagLog(DirectoryPath, _header(), _maxFileBytes);
                }
                catch
                {
                    // Could not open a trace; stay off rather than half-on.
                    _log = null;
                }
            }
            else
            {
                closing = _log;
                _log = null;
            }

            // Persist only what was actually achieved: a failed enable must not
            // be recorded as on, or the next host start would try to resume a
            // trace that could not be opened.
            if ((_log is not null) == enabled)
            {
                try
                {
                    _persist(enabled);
                }
                catch
                {
                    // The state is live either way; persistence is a convenience.
                }
            }
        }

        // Disposed outside the lock: it drains the queue on a budget, and no
        // caller of Write should ever wait behind that.
        closing?.Dispose();
    }

    public void Dispose()
    {
        DiagLog? closing;

        lock (_gate)
        {
            _disposed = true;
            closing = _log;
            _log = null;
        }

        closing?.Dispose();
    }
}
