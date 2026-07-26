using System.Collections.Concurrent;
using System.Threading.Channels;
using MonitorCowboy.Interop;

namespace MonitorCowboy.Core;

/// <summary>
/// Serializes all DDC/CI traffic for one physical monitor. Windows provides no
/// serialization of its own, so every operation for a monitor funnels through
/// this worker's single consumer task.
///
/// The worker owns the physical monitor handle exclusively. Teardown order:
/// <see cref="Complete"/> -> await <see cref="Completion"/> -> the consumer
/// destroys the handle on its way out. Enqueueing after completion reports the
/// op as failed instead of throwing.
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
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();
    private readonly ConcurrentDictionary<byte, uint> _pendingWrites = new();
    private readonly CancellationTokenSource _cts = new();

    public DdcWorker(
        INativeMonitorApi api,
        MonitorEntry entry,
        Action<string, string>? onCapabilitiesRead,
        Action<byte, uint>? onWriteFailed,
        Action<string>? diag = null)
    {
        _api = api;
        _entry = entry;
        _onCapabilitiesRead = onCapabilitiesRead;
        _onWriteFailed = onWriteFailed;
        _diag = diag;
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
            _api.DestroyMonitor(_entry.Handle);
        }
    }

    private async Task ExecuteReadCapabilitiesAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= CapsReadAttempts; attempt++)
        {
            if (_api.TryGetCapabilitiesString(_entry.Handle, out var raw))
            {
                if (CapabilitiesParser.Parse(raw) is { } caps)
                {
                    _entry.ApplyCapabilities(caps);
                    _onCapabilitiesRead?.Invoke(_entry.DevicePath, raw);
                    _diag?.Invoke($"{_entry.FriendlyName}: capabilities parsed, {caps.VcpCodes.Count} VCP codes");
                    return;
                }

                // Readable but unusable — retrying returns the same string.
                _diag?.Invoke($"{_entry.FriendlyName}: capabilities string has no usable vcp() section (length {raw.Length})");
                break;
            }

            _diag?.Invoke($"{_entry.FriendlyName}: capabilities read failed (attempt {attempt}/{CapsReadAttempts}, win32={_api.LastWin32Error})");
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
            _diag?.Invoke($"{_entry.FriendlyName}: VCP probe input={supportsInput} volume={supportsVolume} (last win32={_api.LastWin32Error})");

            if (supportsInput || supportsVolume)
            {
                _entry.ApplyProbedCapabilities(supportsInput, supportsVolume);
                return;
            }
        }

        _entry.MarkCapsReadFailed();
    }

    /// <summary>One retry per probed code: a single transient NAK must not decide support.</summary>
    private async Task<bool> ProbeAsync(byte code, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (_api.TryGetVcpFeature(_entry.Handle, code, out _, out _))
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
            if (_api.TryGetVcpFeature(_entry.Handle, Vcp.InputSource, out var input, out _))
                _entry.ApplyReadValue(Vcp.InputSource, input, 0);
            else
                anyFailed = true;
        }

        if (_entry.SupportsVolume)
        {
            if (_api.TryGetVcpFeature(_entry.Handle, Vcp.AudioSpeakerVolume, out var volume, out var max))
                _entry.ApplyReadValue(Vcp.AudioSpeakerVolume, volume, max);
            else
                anyFailed = true;
        }

        _entry.EndRefresh(anyFailed);
        return true;
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
        if (!_api.TrySetVcpFeature(_entry.Handle, code, target))
        {
            // Toast only when the failure was actually recorded — a newer
            // write may have superseded this target already.
            if (_entry.FinishPendingWrite(code, target, new PendingWrite(target, OpPhase.Failed)))
                _onWriteFailed?.Invoke(code, target);
            return;
        }

        // The set call returning success means nothing by itself; the only
        // trustworthy signal is reading the value back.
        await Task.Delay(PostSetDelay, ct).ConfigureAwait(false);

        for (var attempt = 1; attempt <= VerifyAttempts; attempt++)
        {
            if (_api.TryGetVcpFeature(_entry.Handle, code, out var current, out var max))
            {
                var matches = code == Vcp.InputSource
                    ? InputSourceNames.SameInput(current, target)
                    : current == target;

                if (matches)
                {
                    _entry.ApplyReadValue(code, current, max);
                    _entry.FinishPendingWrite(code, target, null);
                    return;
                }
            }

            if (attempt < VerifyAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
        }

        // Common and expected when the write switched the monitor's input away
        // from this machine; report honestly instead of guessing.
        _entry.FinishPendingWrite(code, target, new PendingWrite(target, OpPhase.Unverified));
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
