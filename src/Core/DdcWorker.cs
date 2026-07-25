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
    private const int VerifyAttempts = 3;

    private readonly INativeMonitorApi _api;
    private readonly MonitorEntry _entry;
    private readonly Action<string, string>? _onCapabilitiesRead;
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();
    private readonly ConcurrentDictionary<byte, uint> _pendingWrites = new();
    private readonly CancellationTokenSource _cts = new();

    public DdcWorker(INativeMonitorApi api, MonitorEntry entry, Action<string, string>? onCapabilitiesRead)
    {
        _api = api;
        _entry = entry;
        _onCapabilitiesRead = onCapabilitiesRead;
        Completion = Task.Run(ConsumeAsync);
    }

    public Task Completion { get; }

    public void RequestReadValues() => _signals.Writer.TryWrite(Signal.ReadValues);

    public void RequestReadCapabilities() => _signals.Writer.TryWrite(Signal.ReadCapabilities);

    /// <summary>
    /// Queue a VCP write. Consecutive writes to the same code are merged
    /// latest-wins: the target lives in a dictionary the consumer drains at
    /// execution time, so stale intermediate values are never sent.
    /// </summary>
    public void RequestWrite(byte code, uint target)
    {
        _pendingWrites[code] = target;
        _entry.SetPendingWrite(code, new PendingWrite(target, OpPhase.Pending));
        if (!_signals.Writer.TryWrite(Signal.Write))
            _entry.SetPendingWrite(code, new PendingWrite(target, OpPhase.Failed));
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
                    switch (signal)
                    {
                        case Signal.ReadCapabilities:
                            ExecuteReadCapabilities();
                            ExecuteReadValues();
                            break;
                        case Signal.ReadValues:
                            ExecuteReadValues();
                            break;
                        case Signal.Write:
                            await ExecuteWritesAsync(ct).ConfigureAwait(false);
                            break;
                    }

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

    private void ExecuteReadCapabilities()
    {
        if (_api.TryGetCapabilitiesString(_entry.Handle, out var raw)
            && CapabilitiesParser.Parse(raw) is { } caps)
        {
            _entry.ApplyCapabilities(caps);
            _onCapabilitiesRead?.Invoke(_entry.DevicePath, raw);
        }
        else
        {
            _entry.MarkCapsUnsupported();
        }
    }

    private void ExecuteReadValues()
    {
        if (_entry.CapsState != CapsState.Ready)
        {
            _entry.EndRefresh(anyReadFailed: false);
            return;
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
    }

    private async Task ExecuteWritesAsync(CancellationToken ct)
    {
        foreach (var code in _pendingWrites.Keys.ToArray())
        {
            if (!_pendingWrites.TryRemove(code, out var target))
                continue;

            await ExecuteOneWriteAsync(code, target, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteOneWriteAsync(byte code, uint target, CancellationToken ct)
    {
        if (!_api.TrySetVcpFeature(_entry.Handle, code, target))
        {
            _entry.SetPendingWrite(code, new PendingWrite(target, OpPhase.Failed));
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
                    _entry.SetPendingWrite(code, null);
                    return;
                }
            }

            if (attempt < VerifyAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
        }

        // Common and expected when the write switched the monitor's input away
        // from this machine; report honestly instead of guessing.
        _entry.SetPendingWrite(code, new PendingWrite(target, OpPhase.Unverified));
    }

    private void FailRemainingWrites()
    {
        foreach (var code in _pendingWrites.Keys.ToArray())
        {
            if (_pendingWrites.TryRemove(code, out var target))
                _entry.SetPendingWrite(code, new PendingWrite(target, OpPhase.Failed));
        }
    }
}
