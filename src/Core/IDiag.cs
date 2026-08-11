namespace MonitorCowboy.Core;

/// <summary>
/// Sink for the structured diagnostic trace.
///
/// Call sites must test <see cref="Enabled"/> before composing a line: with
/// diagnostics off — the shipped default — not even the field string may be
/// built. Implementations must never throw and never block the caller; both
/// the query path and the DDC workers write here.
/// </summary>
public interface IDiag
{
    bool Enabled { get; }

    /// <summary>Records one event.</summary>
    /// <param name="tag">Short monitor identifier ("M1"), or "--" for process-level events.</param>
    /// <param name="code">Dotted event code, e.g. "VCP.GET".</param>
    /// <param name="fields">Space-separated key=value pairs.</param>
    void Write(string tag, string code, string fields);
}

/// <summary>
/// Runtime control surface for the trace, rendered by the <c>debug</c> view.
/// Separate from <see cref="IDiag"/> so that the components doing the tracing
/// hold only the write side and cannot turn themselves on.
/// </summary>
public interface IDiagnosticsControl
{
    bool Enabled { get; }

    string DirectoryPath { get; }

    /// <summary>Path of the trace being written, or null while diagnostics are off.</summary>
    string? FilePath { get; }

    long Written { get; }

    /// <summary>Events discarded because the queue was full — a nonzero value qualifies the trace.</summary>
    long Dropped { get; }

    void SetEnabled(bool enabled);
}

/// <summary>The shipped default: diagnostics off, every call a no-op.</summary>
public sealed class NullDiag : IDiag
{
    public static readonly NullDiag Instance = new();

    private NullDiag()
    {
    }

    public bool Enabled => false;

    public void Write(string tag, string code, string fields)
    {
    }
}
