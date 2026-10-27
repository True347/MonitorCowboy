using System.Text.Json;
using MonitorCowboy;
using MonitorCowboy.Core;
using MonitorCowboy.Diagnostics;
using Xunit;

namespace MonitorCowboy.Tests;

/// <summary>
/// The diagnostics must be invisible when off and harmless when broken: they
/// observe a plugin whose own rule is that the query path does no I/O.
/// </summary>
public class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mc-diag-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Test housekeeping only.
        }
    }

    private string ReadTrace()
        => ReadShared(new DirectoryInfo(_dir).GetFiles("trace-*.log").Single().FullName);

    /// <summary>
    /// Reads a trace that may still be open for writing. File.ReadAllText asks
    /// for FileShare.Read, which on Windows will not coexist with the writer's
    /// write access — and the drain that closes it is bounded, so a loaded
    /// machine can leave it open. Invisible on Linux, where share modes are not
    /// enforced, which is exactly why it belongs here rather than in CI.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void DiagLog_WritesHeaderAndEvents()
    {
        using (var log = new DiagLog(_dir, "== header =="))
        {
            log.Write("M1", "VCP.GET", "code=0x60 ok=1");
            log.Write("--", "QUERY", "ms=0");
        }

        var text = ReadTrace();
        Assert.Contains("== header ==", text);
        Assert.Contains("VCP.GET", text);
        Assert.Contains("code=0x60 ok=1", text);
        Assert.Contains("QUERY", text);
    }

    [Fact]
    public void DiagLog_CountsWhatItWrote()
    {
        using var log = new DiagLog(_dir, "h");
        log.Write("M1", "A", "x=1");
        log.Write("M1", "B", "x=2");

        Assert.Equal(2, log.Written);
        Assert.Equal(0, log.Dropped);
    }

    [Fact]
    public void DiagLog_CreatesItsFolderBeforeReportingAPath()
    {
        // "Open the diagnostics folder" must not point at something that does
        // not exist yet, so the folder cannot be created on the writer task.
        using var log = new DiagLog(_dir, "h");

        Assert.True(Directory.Exists(_dir));
        Assert.StartsWith(_dir, log.FilePath);
    }

    [Fact]
    public void DiagLog_UnusableDirectory_FailsAtConstructionRatherThanHalfOn()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"mc-diag-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");

        try
        {
            Assert.ThrowsAny<IOException>(() => new DiagLog(Path.Combine(blocker, "diagnostics"), "h"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void DiagSwitch_UnusableDirectory_StaysOff()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"mc-diag-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        var persisted = new List<bool>();

        try
        {
            using var sw = new DiagSwitch(Path.Combine(blocker, "diagnostics"), () => "h", persisted.Add);

            sw.SetEnabled(true);

            // Reporting "on" while writing nowhere is the one outcome that
            // would waste a whole support session.
            Assert.False(sw.Enabled);
            Assert.Empty(persisted);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void DiagSwitch_OffByDefault_WritesNothingAndKeepsNoFile()
    {
        var persisted = new List<bool>();
        using var sw = new DiagSwitch(_dir, () => "h", persisted.Add);

        Assert.False(sw.Enabled);
        sw.Write("M1", "A", "x=1"); // must be a no-op, not a crash

        Assert.Null(sw.FilePath);
        Assert.Equal(0, sw.Written);
        Assert.False(Directory.Exists(_dir));
        Assert.Empty(persisted);
    }

    [Fact]
    public void DiagSwitch_TogglingOnAndOff_PersistsBothTransitions()
    {
        var persisted = new List<bool>();
        using var sw = new DiagSwitch(_dir, () => "== header ==", persisted.Add);

        sw.SetEnabled(true);
        Assert.True(sw.Enabled);
        sw.Write("M1", "CAPS.RAW", "len=3");
        var path = sw.FilePath;
        Assert.NotNull(path);

        sw.SetEnabled(false);
        Assert.False(sw.Enabled);
        Assert.Null(sw.FilePath);

        Assert.Equal([true, false], persisted);
        Assert.Contains("CAPS.RAW", ReadShared(path!));
    }

    [Fact]
    public void DiagSwitch_EnablingTwice_KeepsTheSameTrace()
    {
        var persisted = new List<bool>();
        using var sw = new DiagSwitch(_dir, () => "h", persisted.Add);

        sw.SetEnabled(true);
        var first = sw.FilePath;
        sw.SetEnabled(true);

        Assert.Equal(first, sw.FilePath);

        // The persist callback is the proof the second call short-circuited:
        // two traces opened within the same second would share a file name, so
        // counting files could not tell them apart.
        Assert.Equal([true], persisted);
    }

    [Fact]
    public void DiagSwitch_PersistFailure_LeavesTheTraceRunning()
    {
        using var sw = new DiagSwitch(_dir, () => "h", _ => throw new InvalidOperationException("disk full"));

        sw.SetEnabled(true);

        // The live state is what the session needs; persistence is a convenience.
        Assert.True(sw.Enabled);
    }

    [Fact]
    public void DiagLog_PastItsSizeCap_RetiresItself()
    {
        using var log = new DiagLog(_dir, "h", maxFileBytes: 512);

        for (var i = 0; i < 200; i++)
            log.Write("M1", "VCP.GET", "code=0x60 ok=1 cur=0x11 max=17 ms=77");

        SpinWait.SpinUntil(() => !log.Enabled, TimeSpan.FromSeconds(5));
        Assert.False(log.Enabled);
    }

    [Fact]
    public void DiagSwitch_AfterTheTraceRetiresItself_TurnsOnAgain()
    {
        var persisted = new List<bool>();
        using var sw = new DiagSwitch(_dir, () => "h", persisted.Add, maxFileBytes: 512);

        sw.SetEnabled(true);
        var retired = sw.FilePath;

        // Reaching the size cap is the ordinary end of a long support session,
        // not an error. The retired log keeps occupying the field while
        // reporting itself off, and once upon a time that made every route back
        // on a no-op — leaving a restart as the only escape.
        for (var i = 0; i < 200; i++)
            sw.Write("M1", "VCP.GET", "code=0x60 ok=1 cur=0x11 max=17 ms=77");

        SpinWait.SpinUntil(() => !sw.Enabled, TimeSpan.FromSeconds(5));
        Assert.False(sw.Enabled);

        sw.SetEnabled(true);

        Assert.True(sw.Enabled);
        Assert.NotEqual(retired, sw.FilePath);
        Assert.Equal([true, true], persisted);
    }

    [Fact]
    public void DiagSwitch_AfterDispose_StaysOff()
    {
        var sw = new DiagSwitch(_dir, () => "h", _ => { });
        sw.Dispose();

        sw.SetEnabled(true);

        Assert.False(sw.Enabled);
    }

    [Fact]
    public void NullDiag_IsDisabledAndSilent()
    {
        IDiag diag = NullDiag.Instance;

        Assert.False(diag.Enabled);
        diag.Write("M1", "A", "x=1");
    }
}

/// <summary>
/// The capabilities cache is the only state the plugin persists, and a bad file
/// must never be able to keep it from starting.
/// </summary>
public class PluginSettingsTests
{
    [Fact]
    public void CapabilitiesMap_DefaultsToEmpty()
    {
        Assert.Empty(new PluginSettings().CapabilitiesByDevicePath);
    }

    [Fact]
    public void CapabilitiesMap_ExplicitNull_CoalescesToEmpty()
    {
        // The shape a hand-edited or partially written settings file produces:
        // valid JSON, valid object, null map. Flow only guards the null root.
        var settings = new PluginSettings { CapabilitiesByDevicePath = null! };

        Assert.NotNull(settings.CapabilitiesByDevicePath);
        Assert.Empty(settings.CapabilitiesByDevicePath);
    }

    [Fact]
    public void CapsStore_SurvivesANulledMap()
    {
        var settings = new PluginSettings { CapabilitiesByDevicePath = null! };
        var saves = 0;
        var store = new FlowSettingsStore(settings, () => saves++);

        Assert.Null(store.TryGet(@"\\?\DISPLAY#ABC"));

        store.Put(@"\\?\DISPLAY#ABC", "(vcp(60 62))");
        Assert.Equal("(vcp(60 62))", store.TryGet(@"\\?\DISPLAY#ABC"));

        store.Clear();
        Assert.Null(store.TryGet(@"\\?\DISPLAY#ABC"));
        Assert.Equal(2, saves);
    }

    [Fact]
    public void CapsStore_SaveFailure_DoesNotPropagate()
    {
        var store = new FlowSettingsStore(new PluginSettings(), () => throw new IOException("locked"));

        store.Put("path", "caps");

        Assert.Equal("caps", store.TryGet("path"));
    }

    [Fact]
    public void DiagnosticsEnabled_DefaultsToOff()
    {
        Assert.False(new PluginSettings().DiagnosticsEnabled);
    }

    [Fact]
    public void WriteOnlySet_ExplicitNull_CoalescesToEmpty()
    {
        var settings = new PluginSettings { BrightnessWriteOnlyDevicePaths = null! };

        Assert.NotNull(settings.BrightnessWriteOnlyDevicePaths);
        Assert.Empty(settings.BrightnessWriteOnlyDevicePaths);
    }

    [Fact]
    public void WriteOnlySet_RoundTripsThroughJson()
    {
        // Flow persists this object with System.Text.Json; a set must come back as one.
        var settings = new PluginSettings();
        settings.BrightnessWriteOnlyDevicePaths.Add(@"\\?\DISPLAY#ABC");

        var restored = JsonSerializer.Deserialize<PluginSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(@"\\?\DISPLAY#ABC", Assert.Single(restored.BrightnessWriteOnlyDevicePaths));
    }

    [Fact]
    public void Prefs_SaveOnlyOnChange_AndSurviveCapsClear()
    {
        var settings = new PluginSettings();
        var saves = 0;
        var store = new FlowSettingsStore(settings, () => saves++);

        store.SetBrightnessWriteOnly(@"\\?\DISPLAY#ABC", true);
        store.SetBrightnessWriteOnly(@"\\?\DISPLAY#ABC", true);
        Assert.True(store.IsBrightnessWriteOnly(@"\\?\DISPLAY#ABC"));
        Assert.Equal(1, saves);

        // Reload Plugin Data clears the cache, never the user's choice.
        store.Clear();
        Assert.True(store.IsBrightnessWriteOnly(@"\\?\DISPLAY#ABC"));

        store.SetBrightnessWriteOnly(@"\\?\DISPLAY#ABC", false);
        Assert.False(store.IsBrightnessWriteOnly(@"\\?\DISPLAY#ABC"));
        Assert.Equal(3, saves);
    }
}
