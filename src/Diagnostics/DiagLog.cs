using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using MonitorCowboy.Core;

namespace MonitorCowboy.Diagnostics;

/// <summary>
/// The trace file itself: one line per event, drained to disk by a single
/// consumer task so that no caller ever waits on I/O — the query path writes
/// here, and its whole design rule is that it does no I/O.
///
/// Failure policy: a diagnostic must never affect the plugin it is observing.
/// The queue drops rather than grows, every write swallows its own faults, and
/// an I/O error retires the log instead of propagating.
/// </summary>
public sealed class DiagLog : IDiag, IDisposable
{
    private const int QueueCapacity = 4096;
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int KeepFiles = 3;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(500);

    private readonly Channel<string> _lines;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _directory;
    private readonly string _header;
    private readonly Task _writer;

    private long _written;
    private long _dropped;
    private volatile bool _faulted;

    /// <summary>
    /// Throws when the folder cannot be created — deliberately, and before the
    /// object claims to be running: the caller turns diagnostics on only if
    /// there is somewhere to write, and the folder exists by the time
    /// <see cref="FilePath"/> is handed to anything that opens it.
    /// </summary>
    public DiagLog(string directory, string header)
    {
        _directory = directory;
        _header = header;

        Directory.CreateDirectory(directory);
        Prune();

        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            // Losing trace lines is always preferable to growing unbounded
            // inside the host because the disk went slow.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        FilePath = Path.Combine(directory, $"trace-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = Task.Run(ConsumeAsync);
    }

    public string FilePath { get; }

    public long Written => Interlocked.Read(ref _written);

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool Enabled => !_faulted;

    public void Write(string tag, string code, string fields)
    {
        if (_faulted)
            return;

        try
        {
            // Stamped here rather than in the consumer: the timestamp must
            // describe when the event happened, not when it reached the disk.
            // Zero-padded rather than space-padded: the columns still line up,
            // but "t=" stays a single parseable key=value token. Right-aligning
            // with spaces put a gap after the '=' and shifted every field after
            // it for anything splitting on whitespace.
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fff}Z t={_clock.ElapsedMilliseconds:D8} {tag,-3} {code,-16} {fields}");

            if (_lines.Writer.TryWrite(line))
                Interlocked.Increment(ref _written);
            else
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // Never let observing the plugin break the plugin.
        }
    }

    public void Dispose()
    {
        _lines.Writer.TryComplete();

        // Bounded: the tail of a trace is worth a few hundred milliseconds at
        // shutdown, but never more. The consumer only touches the file system,
        // so this cannot deadlock against the UI thread calling us.
        try
        {
            _writer.Wait(DrainBudget);
        }
        catch
        {
            // Nothing left to salvage.
        }
    }

    private async Task ConsumeAsync()
    {
        StreamWriter? file = null;
        try
        {
            file = new StreamWriter(FilePath, append: true) { AutoFlush = false };
            await file.WriteLineAsync(_header).ConfigureAwait(false);
            await file.FlushAsync().ConfigureAwait(false);

            // No cancellation token: Dispose completes the writer, which ends
            // the wait and lets the loop drain what is already queued.
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var line))
                    await file.WriteLineAsync(line).ConfigureAwait(false);

                await file.FlushAsync().ConfigureAwait(false);

                if (file.BaseStream.Length < MaxFileBytes)
                    continue;

                await file.WriteLineAsync("-- size cap reached; trace stops here --").ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
                break;
            }
        }
        catch
        {
            // A trace that cannot be written is retired, not retried: the
            // alternative is an exception per event for the rest of the session.
        }
        finally
        {
            _faulted = true;

            try
            {
                file?.Dispose();
            }
            catch
            {
                // Already tearing down.
            }
        }
    }

    /// <summary>Keeps the newest <see cref="KeepFiles"/> traces, so a long test session cannot fill a disk.</summary>
    private void Prune()
    {
        try
        {
            var stale = new DirectoryInfo(_directory)
                .GetFiles("trace-*.log")
                .OrderByDescending(f => f.Name)
                .Skip(KeepFiles - 1);

            foreach (var f in stale)
                f.Delete();
        }
        catch
        {
            // Pruning is housekeeping; never block a session over it.
        }
    }
}
