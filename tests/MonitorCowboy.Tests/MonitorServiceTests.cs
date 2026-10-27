using MonitorCowboy.Core;
using MonitorCowboy.Interop;
using Xunit;

namespace MonitorCowboy.Tests;

public class MonitorServiceTests
{
    private const string Caps = "(vcp(60(0F 11) 62))";

    private sealed class InMemoryCapsStore : ICapsStore
    {
        private readonly Dictionary<string, string> _map = new();

        public bool Cleared { get; private set; }

        public string? TryGet(string devicePath)
        {
            lock (_map)
                return _map.TryGetValue(devicePath, out var raw) ? raw : null;
        }

        public void Put(string devicePath, string rawCapabilities)
        {
            lock (_map)
                _map[devicePath] = rawCapabilities;
        }

        public void Clear()
        {
            lock (_map)
            {
                _map.Clear();
                Cleared = true;
            }
        }
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
    public async Task Initialize_FiltersInternalPanels_SeedsCachedCaps_AndWarmsUp()
    {
        var api = new FakeNativeMonitorApi
        {
            MonitorsToEnumerate =
            [
                new PhysicalMonitorInfo(new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#EXT#1", "External Monitor", IsInternal: false),
                new PhysicalMonitorInfo(new MonitorRef(@"\\.\DISPLAY2", 0), @"\\?\DISPLAY#INT#1", "Internal Panel", IsInternal: true),
            ],
        };
        api.SetValue(Vcp.InputSource, 0x0F, 0);
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100);

        var store = new InMemoryCapsStore();
        store.Put(@"\\?\DISPLAY#EXT#1", Caps);

        var service = new MonitorService(api, store, (_, _) => { });
        service.Initialize();

        var snapshots = service.GetSnapshots();
        var only = Assert.Single(snapshots);
        Assert.Equal("External Monitor", only.FriendlyName);
        Assert.Equal(CapsState.Ready, only.CapsState); // seeded from the persisted cache

        // Warm-up runs in the background and fills in current values.
        await WaitUntilAsync(() => service.GetSnapshots()[0] is { CurrentInput: 0x0Fu, CurrentVolume: 45u, VolumeMax: 100u });

        service.Dispose();
    }

    [Fact]
    public async Task UnsupportedMonitor_SelfHeals_WhenDdcComesBack()
    {
        // Simulates re-enabling DDC/CI in the OSD: no Windows event fires, so
        // the query-driven retry cadence must pick the monitor back up.
        var api = new FakeNativeMonitorApi
        {
            MonitorsToEnumerate =
            [
                new PhysicalMonitorInfo(new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#EXT#1", "External Monitor", IsInternal: false),
            ],
            Capabilities = null,
            FailGet = true,
        };

        var service = new MonitorService(
            api, new InMemoryCapsStore(), (_, _) => { },
            unsupportedRetryTtl: TimeSpan.FromMilliseconds(50));
        service.Initialize();

        await WaitUntilAsync(() => service.GetSnapshots() is [{ CapsState: CapsState.Unsupported }]);

        // DDC/CI comes back.
        api.Capabilities = Caps;
        api.FailGet = false;
        api.SetValue(Vcp.InputSource, 0x0F, 0);
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100);

        await Task.Delay(75); // let the retry TTL elapse
        service.RequestVolatileRefresh();

        await WaitUntilAsync(() => service.GetSnapshots() is [{ CapsState: CapsState.Ready, CurrentVolume: 45u }]);

        service.Dispose();
    }

    [Fact]
    public async Task Rebuild_WithClearRequest_IsNeverLost_UnderContention()
    {
        var api = new FakeNativeMonitorApi
        {
            MonitorsToEnumerate =
            [
                new PhysicalMonitorInfo(new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#EXT#1", "External Monitor", IsInternal: false),
            ],
            Capabilities = Caps,
        };
        api.SetValue(Vcp.InputSource, 0x0F, 0);
        api.SetValue(Vcp.AudioSpeakerVolume, 45, 100);

        var store = new InMemoryCapsStore();
        store.Put(@"\\?\DISPLAY#EXT#1", Caps);

        var service = new MonitorService(api, store, (_, _) => { });
        service.Initialize();

        // Contend a plain rebuild with a clear-cache rebuild; the coalescing
        // must guarantee the clear runs regardless of which caller wins.
        var plain = service.RebuildTopologyAsync();
        var clearing = service.RebuildTopologyAsync(clearCapsCache: true);
        await Task.WhenAll(plain, clearing);

        await WaitUntilAsync(() => store.Cleared);
        await WaitUntilAsync(() => service.GetSnapshots() is [{ FriendlyName: "External Monitor" }]);

        service.Dispose();
    }

    [Fact]
    public async Task BrightnessWriteOnly_PersistsPerDevicePath_AndSurvivesReload()
    {
        var api = new FakeNativeMonitorApi
        {
            MonitorsToEnumerate =
            [
                new PhysicalMonitorInfo(new MonitorRef(@"\\.\DISPLAY1", 0), @"\\?\DISPLAY#EXT#1", "External Monitor", IsInternal: false),
                new PhysicalMonitorInfo(new MonitorRef(@"\\.\DISPLAY2", 0), @"\\?\DISPLAY#EXT#2", "Other Monitor", IsInternal: false),
            ],
            Capabilities = "(vcp(10 60(0F 11) 62))",
        };
        api.SetValue(Vcp.Luminance, 50, 100);

        var settings = new PluginSettings();
        var store = new FlowSettingsStore(settings, () => { });
        var service = new MonitorService(api, store, (_, _) => { }, prefs: store);
        service.Initialize();
        await WaitUntilAsync(() => service.GetSnapshots() is [{ CapsState: CapsState.Ready }, { CapsState: CapsState.Ready }]);

        Assert.True(service.SetBrightnessWriteOnly(@"\\?\DISPLAY#EXT#1", true));
        Assert.Equal(@"\\?\DISPLAY#EXT#1", Assert.Single(settings.BrightnessWriteOnlyDevicePaths));

        // Reload Plugin Data: the cache is cleared and every entry is rebuilt
        // from scratch. The choice has to come back from the store, and only
        // for the monitor it was made on.
        await service.RebuildTopologyAsync(clearCapsCache: true);
        await WaitUntilAsync(() => service.GetSnapshots() is [{ BrightnessWriteOnly: true }, { BrightnessWriteOnly: false }]);

        Assert.True(service.SetBrightnessWriteOnly(@"\\?\DISPLAY#EXT#1", false));
        Assert.Empty(settings.BrightnessWriteOnlyDevicePaths);
        Assert.True(service.GetSnapshots() is [{ BrightnessWriteOnly: false }, _]);

        service.Dispose();
    }
}
