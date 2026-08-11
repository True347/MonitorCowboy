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
    /// <summary>Size cap per trace. Internal so tests can reach the cap without writing five megabytes.</summary>
    internal const long DefaultMaxFileBytes = 5L * 1024 * 1024;

    private const int QueueCapacity = 4096;
    private const int KeepFiles = 3;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(500);

    private readonly Channel<string> _lines;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _directory;
    private readonly string _header;
    private readonly StreamWriter _file;
    private readonly Task _writer;
    private readonly long _maxFileBytes;

    private long _written;
    private long _dropped;
    private volatile bool _faulted;

    /// <summary>
    /// Throws when the folder cannot be created or the file cannot be opened —
    /// deliberately, and before the object claims to be running: the caller
    /// turns diagnostics on only if there is somewhere to write, and by the
    /// time <see cref="FilePath"/> is handed to anything, that file exists and
    /// is ours.
    /// </summary>
    public DiagLog(string directory, string header, long maxFileBytes = DefaultMaxFileBytes)
    {
        _directory = directory;
        _header = header;
        _maxFileBytes = maxFileBytes;

        Directory.CreateDirectory(directory);
        Prune();

        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            // Losing trace lines is always preferable to growing unbounded
            // inside the host because the disk went slow.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        (FilePath, _file) = OpenTrace(directory);

        try
        {
            _writer = Task.Run(ConsumeAsync);
        }
        catch
        {
            // Realistically only an already-failing process reaches here, and
            // the handle would still be reclaimed by SafeFileHandle's critical
            // finalizer. Close it now anyway: until the writer task exists,
            // nothing else owns this handle, and "eventually, via GC" is not a
            // release policy worth inheriting.
            _file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Claims a trace file, unique by construction rather than by hoping the
    /// clock has moved. Retiring one log and opening the next can complete in
    /// well under a millisecond, so a timestamped name alone collides: two
    /// sessions would be appended into one file, and on Windows the second
    /// open would fail outright and retire the new trace on arrival.
    /// </summary>
    private static (string Path, StreamWriter File) OpenTrace(string directory)
    {
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}";

        for (var n = 0; ; n++)
        {
            var path = Path.Combine(directory, n == 0 ? $"trace-{stamp}.log" : $"trace-{stamp}-{n}.log");
            try
            {
                // ReadWrite, so a support file can be opened by a text editor
                // while it is still being written — on Windows a reader must be
                // permitted to coexist with our write access, and FileShare.Read
                // alone denies that. Deliberately not Delete: refusing deletion
                // of the live trace is what keeps Prune from unlinking it.
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                return (path, new StreamWriter(stream) { AutoFlush = false });
            }
            catch (IOException) when (n < 99 && File.Exists(path))
            {
                // Taken within the same millisecond; step the suffix. Any other
                // I/O failure is a real one and propagates to the caller.
            }
        }
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
        // The file was opened by the constructor, so a trace that exists is
        // already writable: reaching here cannot fail for want of a file.
        var file = _file;
        try
        {
            await file.WriteLineAsync(_header).ConfigureAwait(false);
            await file.FlushAsync().ConfigureAwait(false);

            // No cancellation token: Dispose completes the writer, which ends
            // the wait and lets the loop drain what is already queued.
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var line))
                    await file.WriteLineAsync(line).ConfigureAwait(false);

                await file.FlushAsync().ConfigureAwait(false);

                if (file.BaseStream.Length < _maxFileBytes)
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
                file.Dispose();
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
