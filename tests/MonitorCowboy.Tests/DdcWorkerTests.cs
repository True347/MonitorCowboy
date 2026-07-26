using MonitorCowboy.Core;
using Xunit;

namespace MonitorCowboy.Tests;

public class DdcWorkerTests
{
    private const string CapsWithInputAndVolume = "(vcp(60(0F 11) 62))";

    private static MonitorEntry NewReadyEntry()
    {
        var entry = new MonitorEntry(1, 0x1234, @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        entry.ApplyCapabilities(CapabilitiesParser.Parse(CapsWithInputAndVolume)!);
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
        var failures = new List<(byte Code, uint Target)>();
        var worker = new DdcWorker(api, entry, null, (code, target) => { lock (failures) failures.Add((code, target)); });

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        await WaitUntilAsync(() => entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Failed, Target: 30u });
        await WaitUntilAsync(() => { lock (failures) return failures.Contains((Vcp.AudioSpeakerVolume, 30u)); });

        worker.Complete();
        await worker.Completion;
    }

    [Fact]
    public async Task Write_VerifyMismatch_ReportsUnverified()
    {
        var api = new FakeNativeMonitorApi { ApplyWrites = false };
        api.SetValue(Vcp.AudioSpeakerVolume, 50, 100);
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);

        // 100ms post-set + three verify reads with backoff ≈ 1s worst case.
        await WaitUntilAsync(() => entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Unverified, Target: 30u });

        worker.Complete();
        await worker.Completion;
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
    public async Task Complete_DestroysHandle_AndFailsLateWrites()
    {
        var api = new FakeNativeMonitorApi();
        var entry = NewReadyEntry();
        var worker = new DdcWorker(api, entry, null, null);

        worker.Complete();
        await worker.Completion;
        Assert.Equal(1, api.DestroyedCount);

        worker.RequestWrite(Vcp.AudioSpeakerVolume, 30);
        Assert.True(entry.BuildSnapshot().PendingVolume is { Phase: OpPhase.Failed, Target: 30u });
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public async Task Refresh_CapsNotReady_ClearsFlagWithoutTouchingTtl()
    {
        var api = new FakeNativeMonitorApi();
        var entry = new MonitorEntry(1, 0x1234, @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
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
        var entry = new MonitorEntry(1, 0x1234, @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
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
        var entry = new MonitorEntry(1, 0x1234, @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.BuildSnapshot() is
        {
            CapsState: CapsState.Ready,
            CapsProbed: true,
            SupportsInput: true,
            SupportsVolume: true,
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
        var entry = new MonitorEntry(1, 0x1234, @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
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
    public async Task ReadCapabilities_Success_ChainsValueRead()
    {
        var api = new FakeNativeMonitorApi { Capabilities = CapsWithInputAndVolume };
        api.SetValue(Vcp.InputSource, 0x11, 0);
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100);
        var entry = new MonitorEntry(1, 0x1234, @"\\?\DISPLAY#TEST#1", "Test Monitor", _ => { });
        var worker = new DdcWorker(api, entry, null, null);

        worker.RequestReadCapabilities();

        await WaitUntilAsync(() => entry.BuildSnapshot() is
        {
            CapsState: CapsState.Ready,
            CurrentInput: 0x11u,
            CurrentVolume: 45u,
            VolumeMax: 100u,
        });

        worker.Complete();
        await worker.Completion;
    }
}
