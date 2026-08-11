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

    private volatile DiagLog? _log;
    private bool _disposed;

    /// <param name="directoryPath">Folder the traces live in.</param>
    /// <param name="header">Builds the session header, evaluated when a trace opens.</param>
    /// <param name="persist">Records the new state so it survives a host restart.</param>
    public DiagSwitch(string directoryPath, Func<string> header, Action<bool> persist)
    {
        DirectoryPath = directoryPath;
        _header = header;
        _persist = persist;
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
                if (_log is not null)
                    return;

                try
                {
                    _log = new DiagLog(DirectoryPath, _header());
                }
                catch
                {
                    // Could not open a trace; stay off rather than half-on.
                    _log = null;
                    return;
                }
            }
            else
            {
                closing = _log;
                _log = null;
            }

            try
            {
                _persist(enabled);
            }
            catch
            {
                // The state is live either way; persistence is a convenience.
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
