using MonitorCowboy.Core;
using MonitorCowboy.Interop;
using Xunit;

namespace MonitorCowboy.Tests;

public class DdcWorkerTests
{
    private const string CapsWithAllFeatures = "(vcp(10 60(0F 11) 62))";

    private static MonitorEntry NewReadyEntry()
    {
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        entry.ApplyCapabilities(CapabilitiesParser.Parse(CapsWithAllFeatures)!);
        return entry;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition not met within the timeout");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Write_VerifiedSuccess_UpdatesValueAndClearsMarker()
    {
        var api = new FakeNativeMonitorApi();
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentVolume: 30, PendingVolume: null });
        Assert.Equal([(Vcp.AudioSpeakerVolume, 30u)], api.SetCalls);

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_SetFails_ReportsFailedAndInvokesCallback()
    {
        var api = new FakeNativeMonitorApi { FailSet = true };
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var failures = new List<(byte Code, uint Target, OpPhase Phase)>();
        var worker = new DdcWorker(api, entry, null, (code, target, phase) => { lock (failures) failures.Add((code, target, phase)); });

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Failed, Target: 30u });
        await WaitUntilAsync(() => { lock (failures) return failures.Contains((Vcp.AudioSpeakerVolume, 30u, OpPhase.Failed)); });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_ReadbackMismatch_ReportsRejected()
    {
        // The monitor answers every read and keeps reporting the old value:
        // the write was swallowed, which is a definite Rejected, not an
        // unknown — and the cache must hold the real (old) value, not the target.
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        // 100ms post-set + three verify reads with backoff ≈ 1s worst case.
        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingVolume: { Phase: OpPhase.Rejected, Target: 30u }, CurrentVolume: 50u });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_ReadbackFails_ReportsUnverified_WithoutCallback()
    {
        // The set goes through but every read after it fails (input switched
        // away, monitor asleep): genuinely unknown — Unverified, and silent.
        var api = new FakeNativeMonitorApi();
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var callbacks = 0;
        var worker = new DdcWorker(api, entry, null, (_, _, _) => Interlocked.Increment(ref callbacks));

        api.BeforeSet = () => api.FailGet = true;
        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Unverified, Target: 30u });
        Assert.Equal(0, Volatile.Read(ref callbacks));

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_EarlyMismatchThenDeadReads_ReportsUnverified_NotRejected()
    {
        // The canonical input-switch shape: the first verify read answers with
        // the old value, then the channel dies. Classification keys off the
        // FINAL attempt, so this must stay Unverified (silent) — flagging it
        // Rejected would toast a false "did not switch" / "locked" alarm and
        // stamp the pre-switch value back into the cache.
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var callbacks = 0;
        var worker = new DdcWorker(api, entry, null, (_, _, _) => Interlocked.Increment(ref callbacks));

        var verifyReads = 0;
        api.BeforeSet = () => api.BeforeGet = () =>
        {
            if (++verifyReads > 1)
                api.FailGet = true;
        };

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Unverified, Target: 30u });
        Assert.Equal(0, Volatile.Read(ref callbacks));

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_BrightnessSwallowed_ReportsRejected_AndInvokesCallback()
    {
        // The direct evidence for "a locked brightness write never fails
        // silently": Rejected badge, real value refreshed, callback raised.
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        var failures = new List<(byte Code, uint Target, OpPhase Phase)>();
        var worker = new DdcWorker(api, entry, null, (code, target, phase) => { lock (failures) failures.Add((code, target, phase)); });

        worker.RequestWrite(Vcp.Luminance, 80);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingBrightness: { Phase: OpPhase.Rejected, Target: 80u }, CurrentBrightness: 50u });
        await WaitUntilAsync(() => { lock (failures) return failures.Contains((Vcp.Luminance, 80u, OpPhase.Rejected)); });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_BrightnessClampedWithinTolerance_Verifies()
    {
        // A panel that stores 79 for 80 must not raise a false "locked" alarm.
        var api = new FakeNativeMonitorApi { WriteTransform = (_, value) => value - 1 };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.Luminance, 80);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentBrightness: 79u, PendingBrightness: null });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_BrightnessClampedBeyondTolerance_Rejects()
    {
        var api = new FakeNativeMonitorApi { WriteTransform = (_, value) => value - 5 };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.Luminance, 80);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingBrightness: { Phase: OpPhase.Rejected, Target: 80u }, CurrentBrightness: 75u });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_VolumeExactMatchStillRequired()
    {
        // The brightness tolerance must not leak into volume: an off-by-one
        // readback there stays a rejection, exactly as shipped.
        var api = new FakeNativeMonitorApi { WriteTransform = (_, value) => value - 1 };
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Rejected, Target: 30u });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task BrightnessRejection_PersistsAcrossReadback_ClearsOnVerifiedWrite()
    {
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.Luminance, 80);
        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingBrightness.Phase: OpPhase.Rejected, BrightnessRecentlyRejected: true });

        // The next background read clears the badge but must keep the hint:
        // reads succeed even while brightness is locked, so a read proves nothing.
        entry.ApplyReadValue(Vcp.Luminance, 50, 100);
        Assert.True(entry.BuildSnapshot() is { PendingBrightness: null, BrightnessRecentlyRejected: true });

        // Only a verified write proves the lock is gone.
        api.ApplyWrites = true;
        worker.RequestWrite(Vcp.Luminance, 80);
        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentBrightness: 80u, PendingBrightness: null, BrightnessRecentlyRejected: false });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task BrightnessWriteOnly_SkipsReadback_RecordsTargetSilently()
    {
        // The field-reported shape: the monitor applies the write but keeps
        // reporting the value it had. With write-only on, no verify read may
        // run, the target becomes the current value, and nothing is raised.
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        entry.SetBrightnessWriteOnly(true);
        var callbacks = 0;
        var worker = new DdcWorker(api, entry, null, (_, _, _) => Interlocked.Increment(ref callbacks));

        var readsAfterSet = 0;
        api.BeforeSet = () => api.BeforeGet = () => Interlocked.Increment(ref readsAfterSet);

        worker.RequestWrite(Vcp.Luminance, 80);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentBrightness: 80u, BrightnessAssumed: true, PendingBrightness: null, BrightnessRecentlyRejected: false });
        Assert.Equal([(Vcp.Luminance, 80u)], api.SetCalls);
        Assert.Equal(0, Volatile.Read(ref readsAfterSet));
        Assert.Equal(0, Volatile.Read(ref callbacks));

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task BrightnessWriteOnly_BackgroundRead_KeepsValueLastSet()
    {
        // What makes the steps compound on such a monitor: the TTL refresh
        // keeps reading the stuck value, and it must not replace the one set.
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        entry.SetBrightnessWriteOnly(true);
        var worker = new DdcWorker(api, entry, null, null);

        // Before any write the reported value is all there is, and it is shown.
        Assert.True(entry.TryBeginRefresh());
        worker.RequestReadValues();
        await WaitUntilAsync(() => entry.BuildSnapshot() is { RefreshInFlight: false, CurrentBrightness: 50u, BrightnessAssumed: false });

        worker.RequestWrite(Vcp.Luminance, 80);
        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentBrightness: 80u, BrightnessAssumed: true });

        Assert.True(entry.TryBeginRefresh());
        worker.RequestReadValues();
        await WaitUntilAsync(() => !entry.BuildSnapshot().RefreshInFlight);
        Assert.True(entry.BuildSnapshot() is { CurrentBrightness: 80u, BrightnessMax: 100u, BrightnessAssumed: true });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task BrightnessWriteOnly_SetFails_StillReportsFailed()
    {
        // Write-only gives up the read-back, not the return code: a set the
        // driver refuses is still a failure, badge and callback both.
        var api = new FakeNativeMonitorApi { FailSet = true };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        entry.SetBrightnessWriteOnly(true);
        var failures = new List<(byte Code, uint Target, OpPhase Phase)>();
        var worker = new DdcWorker(api, entry, null, (code, target, phase) => { lock (failures) failures.Add((code, target, phase)); });

        worker.RequestWrite(Vcp.Luminance, 80);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingBrightness: { Phase: OpPhase.Failed, Target: 80u }, BrightnessAssumed: false });
        await WaitUntilAsync(() => { lock (failures) return failures.Contains((Vcp.Luminance, 80u, OpPhase.Failed)); });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task BrightnessWriteOnly_DoesNotLeakIntoVolume()
    {
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        entry.SetBrightnessWriteOnly(true);
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingVolume: { Phase: OpPhase.Rejected, Target: 30u }, CurrentVolume: 50u });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task BrightnessWriteOnly_Toggle_StartsEachModeClean()
    {
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.Luminance, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        // Verified mode first: the stuck read-back is a rejection, as shipped.
        worker.RequestWrite(Vcp.Luminance, 80);
        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingBrightness.Phase: OpPhase.Rejected, BrightnessRecentlyRejected: true });

        // Turning it on retires that evidence: it is exactly the false alarm
        // the mode exists for.
        entry.SetBrightnessWriteOnly(true);
        Assert.True(entry.BuildSnapshot() is { BrightnessWriteOnly: true, PendingBrightness: null, BrightnessRecentlyRejected: false });

        worker.RequestWrite(Vcp.Luminance, 70);
        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentBrightness: 70u, BrightnessAssumed: true });

        // Turning it off drops the assumed value rather than passing it off
        // as read, and the next write is verified again.
        entry.SetBrightnessWriteOnly(false);
        Assert.True(entry.BuildSnapshot() is { BrightnessWriteOnly: false, BrightnessAssumed: false, CurrentBrightness: null });

        worker.RequestWrite(Vcp.Luminance, 60);
        await WaitUntilAsync(() => entry.BuildSnapshot() is { PendingBrightness: { Phase: OpPhase.Rejected, Target: 60u }, CurrentBrightness: 50u });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public void AssumedBrightness_NeverOutlivesWriteOnlyMode()
    {
        // A write that skipped verification but lands after the mode was
        // turned off must not be recorded: the monitor is believed again.
        var entry = NewReadyEntry();
        entry.ApplyReadValue(Vcp.Luminance, 50, 100);

        entry.ApplyAssumedBrightness(80);

        Assert.True(entry.BuildSnapshot() is { CurrentBrightness: 50u, BrightnessAssumed: false });
    }

    [Fact]
    public async Task Writes_SameCode_CoalesceLatestWins()
    {
        var api = new FakeNativeMonitorApi();
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();

        // Block the consumer inside the first set so later writes queue up.
        // A dedicated 'entered' event signals arrival BEFORE the block — the
        // SetCalls count only increments after it, so it cannot be the marker.
        using var entered = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        var firstSet = true;
        api.BeforeSet = () =>
        {
            if (firstSet)
            {
                firstSet = false;
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var worker = new DdcWorker(api, entry, null, null);
        worker.RequestWrite(Vcp.AudioSpeakerVolume, 10);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "consumer never reached the first set");

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 20);
        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);
        gate.Set();

        await WaitUntilAsync(() => entry.BuildSnapshot() is { CurrentVolume: 30, PendingVolume: null });

        // The intermediate 20 must have been merged away.
        Assert.Equal([(Vcp.AudioSpeakerVolume, 10u), (Vcp.AudioSpeakerVolume, 30u)], api.SetCalls);

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Complete_FailsLateWritesHonestly()
    {
        var api = new FakeNativeMonitorApi();
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.Complete();
        await worker.Completion;

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);
        Assert.True(entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Failed, Target: 30u });
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public async Task Refresh_CapsNotReady_ClearsFlagWithoutTouchingTtl()
    {
        var api = new FakeNativeMonitorApi();
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        Assert.True(entry.TryBeginRefresh());
        worker.RequestReadValues();

        await WaitUntilAsync(() => !entry.BuildSnapshot().RefreshInFlight);
        Assert.Equal(DateTime.MinValue, entry.LastValuesReadUtc);

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task CapsReread_Failure_KeepsKnownGoodCaps()
    {
        var api = new FakeNativeMonitorApi { Capabilities = null }; // re-read will fail
        var entry = NewReadyEntry();

        entry.ResetCapsPending();
        Assert.Equal(CapsState.Pending, entry.CapsState);

        var worker = new DdcWorker(api, entry, null, null);
        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.CapsState == CapsState.Ready);
        Assert.True(entry.SupportsVolume); // the known-good map survived the failed re-read

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task ReadCapabilities_FirstSightFailure_NoVcpAnswer_MarksUnsupported()
    {
        var api = new FakeNativeMonitorApi { Capabilities = null, FailGet = true };
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.CapsState == CapsState.Unsupported);

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task ReadCapabilities_Failure_ProbesVcpAndBecomesReady()
    {
        // Capabilities unreadable, but the monitor answers VCP directly — the
        // common real-world case the probe fallback exists for.
        var api = new FakeNativeMonitorApi { Capabilities = null };
        api.SetValue(Vcp.InputSource, 0x11, 0);
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100);
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.BuildSnapshot() is
        {
            CapsState: CapsState.Ready,
            CapsProbed: true,
            SupportsInput: true,
            SupportsVolume: true,
            SupportsBrightness: false, // no 0x10 answer stored — the probe must not invent it
            CurrentInput: 0x11u,
            CurrentVolume: 45u,
        });
        Assert.Equal(InputSourceNames.CommonProbeValues, entry.BuildSnapshot().InputValues);

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task ReadCapabilities_Failure_ProbeVolumeOnly_HidesInput()
    {
        var api = new FakeNativeMonitorApi { Capabilities = null };
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100); // input probe will fail (no value stored)
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.BuildSnapshot() is
        {
            CapsState: CapsState.Ready,
            CapsProbed: true,
            SupportsInput: false,
            SupportsVolume: true,
        });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task ReadCapabilities_Failure_ProbeBrightnessOnly_HidesOtherFeatures()
    {
        var api = new FakeNativeMonitorApi { Capabilities = null };
        api.SetValue(Vcp.Luminance, 60, 100); // input and volume probes will fail (no values stored)
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.BuildSnapshot() is
        {
            CapsState: CapsState.Ready,
            CapsProbed: true,
            SupportsInput: false,
            SupportsVolume: false,
            SupportsBrightness: true,
            CurrentBrightness: 60u,
            BrightnessMax: 100u,
        });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task ReadCapabilities_Success_ChainsValueRead()
    {
        var api = new FakeNativeMonitorApi { Capabilities = CapsWithAllFeatures };
        api.SetValue(Vcp.InputSource, 0x11, 0);
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100);
        api.SetValue(Vcp.Luminance, 60, 100);
        var entry = new MonitorEntry(1, new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.BuildSnapshot() is
        {
            CapsState: CapsState.Ready,
            CurrentInput: 0x11u,
            CurrentVolume: 45u,
            VolumeMax: 100u,
            CurrentBrightness: 60u,
            BrightnessMax: 100u,
        });

        worker.Complete();
        await worker.Completion;
    }
}
