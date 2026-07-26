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
}
