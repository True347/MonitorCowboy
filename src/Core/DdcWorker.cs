using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MonitorCowboy.Interop;

namespace MonitorCowboy.Core;

/// <summary>
/// Serializes all DDC/CI traffic for one physical monitor. Windows provides no
/// serialization of its own, so every operation for a monitor funnels through
/// this worker's single consumer task. No handle is held: the interop layer
/// acquires one fresh per operation. Teardown order: <see cref="Complete"/> ->
/// await <see cref="Completion"/>. Enqueueing after completion reports the op
/// as failed instead of throwing.
/// </summary>
public sealed class DdcWorker
{
    private enum Signal { ReadValues, ReadCapabilities, Write }

    private static readonly TimeSpan InterOpDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan PostSetDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CapsRetryDelay = TimeSpan.FromMilliseconds(300);
    private const int VerifyAttempts = 3;
    private const int CapsReadAttempts = 2;

    private readonly INativeMonitorApi _api;
    private readonly MonitorEntry _entry;
    private readonly Action<string, string>? _onCapabilitiesRead;
    private readonly Action<byte, uint>? _onWriteFailed;
    private readonly Action<string>? _diag;
    private readonly IDiag _trace;
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();
    private readonly ConcurrentDictionary<byte, uint> _pendingWrites = new();
    private readonly CancellationTokenSource _cts = new();

    public DdcWorker(
        INativeMonitorApi api,
        MonitorEntry entry,
        Action<string, string>? onCapabilitiesRead,
        Action<byte, uint>? onWriteFailed,
        Action<string>? diag = null,
        IDiag? trace = null)
    {
        _api = api;
        _entry = entry;
        _onCapabilitiesRead = onCapabilitiesRead;
        _onWriteFailed = onWriteFailed;
        _diag = diag;
        _trace = trace ?? NullDiag.Instance;
        Completion = Task.Run(ConsumeAsync);
    }

    public Task Completion { get; }

    public bool RequestReadValues()
    {
        if (_signals.Writer.TryWrite(Signal.ReadValues))
            return true;
        // Never let the refresh-in-flight flag stick on a torn-down worker.
        _entry.CancelRefresh();
        return false;
    }

    public bool RequestReadCapabilities() => _signals.Writer.TryWrite(Signal.ReadCapabilities);

    /// <summary>
    /// Queue a VCP write. Consecutive writes to the same code are merged
    /// latest-wins: the target lives in a dictionary the consumer drains at
    /// execution time, so stale intermediate values are never sent.
    /// </summary>
    public void RequestWrite(byte code, uint target)
    {
        // Badge before publishing: once the consumer observes the dictionary
        // entry it must also observe the Pending marker, or the outcome it
        // reports through FinishPendingWrite would find no marker and be lost.
        _entry.SetPendingWrite(code, new PendingWrite(target, OpPhase.Pending));
        _pendingWrites[code] = target;
        if (!_signals.Writer.TryWrite(Signal.Write))
        {
            // Worker already completed: withdraw the op so a still-draining
            // consumer cannot send it mid-teardown, then report honestly.
            // FinishPendingWrite (not SetPendingWrite) so a racing newer
            // write's Pending badge is never stomped by this failure.
            _pendingWrites.TryRemove(new KeyValuePair<byte, uint>(code, target));
            _entry.FinishPendingWrite(code, target, new PendingWrite(target, OpPhase.Failed));
        }
    }

    /// <summary>Stop accepting work. Queued-but-unexecuted ops end as failed; delays are cut short.</summary>
    public void Complete()
    {
        _signals.Writer.TryComplete();
        _cts.Cancel();
    }

    private async Task ConsumeAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (await _signals.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_signals.Reader.TryRead(out var signal))
                {
                    var didIo = false;
                    try
                    {
                        switch (signal)
                        {
                            case Signal.ReadCapabilities:
                                await ExecuteReadCapabilitiesAsync(ct).ConfigureAwait(false);
                                ExecuteReadValues();
                                didIo = true;
                                break;
                            case Signal.ReadValues:
                                didIo = ExecuteReadValues();
                                break;
                            case Signal.Write:
                                didIo = await ExecuteWritesAsync(ct).ConfigureAwait(false);
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // State mutations invoke StateChanged subscribers
                        // synchronously on this task; a throwing subscriber
                        // must not kill the monitor's DDC pump.
                    }

                    // Only pace actual bus traffic. A burst of coalesced write
                    // signals leaves a tail of no-op drains that must not
                    // head-of-line block later ops for 75 ms each.
                    if (didIo)
                        await Task.Delay(InterOpDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Teardown requested mid-operation; fall through to cleanup.
        }
        finally
        {
            FailRemainingWrites();
        }
    }

    private async Task ExecuteReadCapabilitiesAsync(CancellationToken ct)
    {
        if (_trace.Enabled)
            _trace.Write(_entry.Tag, "CAPS.REQ", $"name=\"{_entry.FriendlyName}\" known={_entry.HasKnownCaps}");

        for (var attempt = 1; attempt <= CapsReadAttempts; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            if (_api.TryGetCapabilitiesString(_entry.Ref, out var raw))
            {
                if (_trace.Enabled)
                {
                    // The literal string is the whole point of tracing this:
                    // it is the only way to get a real-hardware fixture for the
                    // parser, and every parse claim is judged against it.
                    _trace.Write(_entry.Tag, "CAPS.RAW", $"ms={Ms(started)} len={raw.Length} text={Sanitize(raw)}");
                }

                if (CapabilitiesParser.Parse(raw) is { } caps)
                {
                    _entry.ApplyCapabilities(caps);
                    _onCapabilitiesRead?.Invoke(_entry.DevicePath, raw);
                    _diag?.Invoke($"{_entry.FriendlyName}: capabilities parsed, {caps.VcpCodes.Count} VCP codes");

                    if (_trace.Enabled)
                    {
                        var inputs = string.Join(',', caps.ValuesFor(Vcp.InputSource).Select(v => $"0x{v:X2}"));
                        _trace.Write(
                            _entry.Tag,
                            "CAPS.PARSE",
                            $"codes={caps.VcpCodes.Count} input={caps.Supports(Vcp.InputSource)} volume={caps.Supports(Vcp.AudioSpeakerVolume)} values=[{inputs}]");
                        _trace.Write(_entry.Tag, "CAPS.STATE", "state=Ready probed=False");
                    }

                    return;
                }

                // Readable but unusable — retrying returns the same string.
                _diag?.Invoke($"{_entry.FriendlyName}: capabilities string has no usable vcp() section (length {raw.Length})");
                if (_trace.Enabled)
                    _trace.Write(_entry.Tag, "CAPS.NOVCP", $"len={raw.Length}");
                break;
            }

            _diag?.Invoke($"{_entry.FriendlyName}: capabilities read failed (attempt {attempt}/{CapsReadAttempts}, win32={Win32()})");
            if (_trace.Enabled)
                _trace.Write(_entry.Tag, "CAPS.FAIL", $"attempt={attempt}/{CapsReadAttempts} ms={Ms(started)} win32={Win32()}");

            if (attempt < CapsReadAttempts)
                await Task.Delay(CapsRetryDelay, ct).ConfigureAwait(false);
        }

        // The capabilities exchange is the most fragile DDC/CI command; plenty
        // of monitors fail it while answering VCP get/set just fine. Probe the
        // two features directly before giving up — but only on first sight; a
        // re-read failure falls back to the known-good map instead.
        if (!_entry.HasKnownCaps)
        {
            var supportsInput = await ProbeAsync(Vcp.InputSource, ct).ConfigureAwait(false);
            var supportsVolume = await ProbeAsync(Vcp.AudioSpeakerVolume, ct).ConfigureAwait(false);
            _diag?.Invoke($"{_entry.FriendlyName}: VCP probe input={supportsInput} volume={supportsVolume} (last win32={Win32()})");

            if (_trace.Enabled)
                _trace.Write(_entry.Tag, "CAPS.PROBE", $"input={supportsInput} volume={supportsVolume} win32={Win32()}");

            if (supportsInput || supportsVolume)
            {
                _entry.ApplyProbedCapabilities(supportsInput, supportsVolume);
                if (_trace.Enabled)
                    _trace.Write(_entry.Tag, "CAPS.STATE", "state=Ready probed=True");
                return;
            }
        }

        _entry.MarkCapsReadFailed();
        if (_trace.Enabled)
            _trace.Write(_entry.Tag, "CAPS.STATE", $"state={_entry.CapsState} probed=False fellBackToKnown={_entry.HasKnownCaps}");
    }

    /// <summary>One retry per probed code: a single transient NAK must not decide support.</summary>
    private async Task<bool> ProbeAsync(byte code, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (_api.TryGetVcpFeature(_entry.Ref, code, out _, out _))
                return true;
            await Task.Delay(InterOpDelay, ct).ConfigureAwait(false);
        }

        return false;
    }

    private bool ExecuteReadValues()
    {
        if (_entry.CapsState != CapsState.Ready)
        {
            // No read happened — clear the flag without touching the TTL
            // timestamp, or a real refresh would be masked for a whole TTL
            // once capabilities become ready.
            _entry.CancelRefresh();
            return false;
        }

        var anyFailed = false;

        if (_entry.SupportsInput)
        {
            if (Get(Vcp.InputSource, out var input, out _))
                _entry.ApplyReadValue(Vcp.InputSource, input, 0);
            else
                anyFailed = true;
        }

        if (_entry.SupportsVolume)
        {
            if (Get(Vcp.AudioSpeakerVolume, out var volume, out var max))
                _entry.ApplyReadValue(Vcp.AudioSpeakerVolume, volume, max);
            else
                anyFailed = true;
        }

        _entry.EndRefresh(anyFailed);
        return true;
    }

    /// <summary>
    /// Timed VCP read. The measurement spans the process-wide DDC gate as well
    /// as the exchange itself, which is deliberate: contention is exactly what
    /// makes a "40 ms read" not cost 40 ms.
    /// </summary>
    private bool Get(byte code, out uint current, out uint max)
    {
        var started = Stopwatch.GetTimestamp();
        var ok = _api.TryGetVcpFeature(_entry.Ref, code, out current, out max);

        if (_trace.Enabled)
        {
            _trace.Write(
                _entry.Tag,
                "VCP.GET",
                ok
                    ? $"code=0x{code:X2} ok=1 cur={Val(code, current)} max={max} ms={Ms(started)}"
                    : $"code=0x{code:X2} ok=0 ms={Ms(started)} win32={Win32()}");
        }

        return ok;
    }

    private async Task<bool> ExecuteWritesAsync(CancellationToken ct)
    {
        var any = false;
        foreach (var code in _pendingWrites.Keys.ToArray())
        {
            if (!_pendingWrites.TryRemove(code, out var target))
                continue;

            any = true;
            await ExecuteOneWriteAsync(code, target, ct).ConfigureAwait(false);
        }

        return any;
    }

    private async Task ExecuteOneWriteAsync(byte code, uint target, CancellationToken ct)
    {
        var opStarted = Stopwatch.GetTimestamp();
        var setStarted = opStarted;

        if (!_api.TrySetVcpFeature(_entry.Ref, code, target))
        {
            if (_trace.Enabled)
            {
                _trace.Write(_entry.Tag, "VCP.SET", $"code=0x{code:X2} target={Val(code, target)} ok=0 ms={Ms(setStarted)} win32={Win32()}");
                _trace.Write(_entry.Tag, "WRITE.END", $"code=0x{code:X2} target={Val(code, target)} outcome=failed ms={Ms(opStarted)}");
            }

            // Toast only when the failure was actually recorded — a newer
            // write may have superseded this target already.
            if (_entry.FinishPendingWrite(code, target, new PendingWrite(target, OpPhase.Failed)))
                _onWriteFailed?.Invoke(code, target);
            return;
        }

        if (_trace.Enabled)
            _trace.Write(_entry.Tag, "VCP.SET", $"code=0x{code:X2} target={Val(code, target)} ok=1 ms={Ms(setStarted)}");

        // The set call returning success means nothing by itself; the only
        // trustworthy signal is reading the value back.
        await Task.Delay(PostSetDelay, ct).ConfigureAwait(false);

        for (var attempt = 1; attempt <= VerifyAttempts; attempt++)
        {
            var readStarted = Stopwatch.GetTimestamp();
            var ok = _api.TryGetVcpFeature(_entry.Ref, code, out var current, out var max);
            var matches = ok && (code == Vcp.InputSource
                ? InputSourceNames.SameInput(current, target)
                : current == target);

            if (_trace.Enabled)
            {
                _trace.Write(
                    _entry.Tag,
                    "VERIFY",
                    ok
                        ? $"code=0x{code:X2} target={Val(code, target)} attempt={attempt}/{VerifyAttempts} read={Val(code, current)} match={matches} ms={Ms(readStarted)}"
                        : $"code=0x{code:X2} target={Val(code, target)} attempt={attempt}/{VerifyAttempts} read=fail ms={Ms(readStarted)} win32={Win32()}");
            }

            if (matches)
            {
                _entry.ApplyReadValue(code, current, max);
                _entry.FinishPendingWrite(code, target, null);
                if (_trace.Enabled)
                    _trace.Write(_entry.Tag, "WRITE.END", $"code=0x{code:X2} target={Val(code, target)} outcome=applied attempts={attempt} ms={Ms(opStarted)}");
                return;
            }

            if (attempt < VerifyAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
        }

        // Common and expected when the write switched the monitor's input away
        // from this machine; report honestly instead of guessing.
        _entry.FinishPendingWrite(code, target, new PendingWrite(target, OpPhase.Unverified));
        if (_trace.Enabled)
            _trace.Write(_entry.Tag, "WRITE.END", $"code=0x{code:X2} target={Val(code, target)} outcome=unverified attempts={VerifyAttempts} ms={Ms(opStarted)}");
    }

    private static long Ms(long startTimestamp) => (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Formats a VCP value the way that code is actually read. Input source is
    /// an enumerated code and belongs in hex; speaker volume is a continuous
    /// value over 0..max and belongs in decimal, so that a trace line can be
    /// compared against what the UI showed without converting anything.
    /// </summary>
    private static string Val(byte code, uint value)
        => code == Vcp.InputSource ? $"0x{value:X2}" : value.ToString();

    /// <summary>
    /// Win32 errors from the display driver are HRESULT-shaped and surface as
    /// large negative integers; hex is how they are documented and searched.
    /// </summary>
    private string Win32() => $"0x{_api.LastWin32Error:X8}";

    /// <summary>
    /// Capabilities strings are vendor ASCII and have been seen carrying stray
    /// control bytes; a trace line must stay one line and stay parseable.
    /// </summary>
    private static string Sanitize(string raw)
    {
        var buffer = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            buffer[i] = raw[i] is '\r' or '\n' or '\t' || char.IsControl(raw[i]) ? '.' : raw[i];
        return new string(buffer);
    }

    private void FailRemainingWrites()
    {
        foreach (var code in _pendingWrites.Keys.ToArray())
        {
            if (_pendingWrites.TryRemove(code, out var target))
                _entry.FinishPendingWrite(code, target, new PendingWrite(target, OpPhase.Failed));
        }
    }
}
