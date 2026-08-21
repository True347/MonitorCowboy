using MonitorCowboy.Core;
using MonitorCowboy.Ui;
using Xunit;

namespace MonitorCowboy.Tests;

public class QueryRouterTests
{
    private static MonitorSnapshot Monitor(int index, string name, bool input = true, bool volume = true, bool brightness = true)
        => new()
        {
            Index = index,
            DevicePath = $"\\\\?\\DISPLAY#TEST{index}",
            FriendlyName = name,
            CapsState = CapsState.Ready,
            SupportsInput = input,
            SupportsVolume = volume,
            SupportsBrightness = brightness,
        };

    // Every name contains a digit so ordinal-vs-name priority is actually exercised.
    private static readonly IReadOnlyList<MonitorSnapshot> Three =
    [
        Monitor(1, "DELL U2723QE"),
        Monitor(2, "BenQ EW3270U"),
        Monitor(3, "LG 27UP850"),
    ];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \t")]
    public void BlankSearch_ListsAllMonitors(string search)
    {
        var intent = Assert.IsType<MonitorListIntent>(QueryRouter.Parse(search, Three));
        Assert.Equal("", intent.Filter);
    }

    [Fact]
    public void OrdinalToken_SelectsMonitorByPosition()
    {
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("2", Three));
        Assert.Same(Three[1], intent.Monitor);
    }

    [Fact]
    public void OrdinalToken_TakesPriorityOverNumericNames()
    {
        // "2" appears in all three names; it must still resolve as ordinal #2.
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("2", Three));
        Assert.Same(Three[1], intent.Monitor);

        // "27" matches two names as a substring but is out of range as an
        // ordinal; digits never fall through to name matching.
        var fallback = Assert.IsType<MonitorListIntent>(QueryRouter.Parse("27", Three));
        Assert.Equal("27", fallback.Filter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("99999999999999999999")]
    public void OrdinalOutOfRange_FallsBackToFilteredList(string search)
    {
        var intent = Assert.IsType<MonitorListIntent>(QueryRouter.Parse(search, Three));
        Assert.Equal(search, intent.Filter);
    }

    [Theory]
    [InlineData("benq")]
    [InlineData("BENQ")]
    [InlineData("ew32")]
    public void UniqueNameSubstring_SelectsThatMonitor(string search)
    {
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse(search, Three));
        Assert.Same(Three[1], intent.Monitor);
    }

    [Fact]
    public void AmbiguousNameSubstring_FallsBackToFilteredList()
    {
        IReadOnlyList<MonitorSnapshot> twoDells =
        [
            Monitor(1, "DELL U2723QE"),
            Monitor(2, "DELL P2422H"),
        ];
        var intent = Assert.IsType<MonitorListIntent>(QueryRouter.Parse("dell", twoDells));
        Assert.Equal("dell", intent.Filter);
    }

    [Fact]
    public void UnmatchedNameSubstring_FallsBackToFilteredList()
    {
        var intent = Assert.IsType<MonitorListIntent>(QueryRouter.Parse("asus something", Three));
        Assert.Equal("asus something", intent.Filter);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("dell")]
    [InlineData("anything at all")]
    public void EmptyMonitorList_AlwaysListsWithTrimmedFilter(string search)
    {
        var intent = Assert.IsType<MonitorListIntent>(QueryRouter.Parse($"  {search}  ", []));
        Assert.Equal(search, intent.Filter);
    }

    [Theory]
    [InlineData("in")]
    [InlineData("In")]
    [InlineData("INPUT")]
    public void InputSynonyms_RouteToInputMenu(string sub)
    {
        var intent = Assert.IsType<InputMenuIntent>(QueryRouter.Parse($"1 {sub}", Three));
        Assert.Same(Three[0], intent.Monitor);
        Assert.Equal("", intent.Filter);
    }

    [Fact]
    public void InputFilter_JoinsRemainingTokensWithSingleSpaces()
    {
        var intent = Assert.IsType<InputMenuIntent>(QueryRouter.Parse("1 in hd mi", Three));
        Assert.Same(Three[0], intent.Monitor);
        Assert.Equal("hd mi", intent.Filter);
    }

    [Fact]
    public void NameResolvedMonitor_SupportsSubCommands()
    {
        var intent = Assert.IsType<InputMenuIntent>(QueryRouter.Parse("lg input hdmi", Three));
        Assert.Same(Three[2], intent.Monitor);
        Assert.Equal("hdmi", intent.Filter);
    }

    [Theory]
    [InlineData("vol")]
    [InlineData("VOLUME")]
    [InlineData("v")]
    [InlineData("V")]
    public void VolumeSynonyms_RouteToVolumeMenu(string sub)
    {
        var intent = Assert.IsType<VolumeMenuIntent>(QueryRouter.Parse($"3 {sub}", Three));
        Assert.Same(Three[2], intent.Monitor);
        Assert.Equal("", intent.ValueToken);
    }

    [Theory]
    [InlineData("30", "30")]
    [InlineData("150", "150")]
    [InlineData("abc", "abc")]
    public void VolumeValueToken_PassesThroughUnvalidated(string token, string expected)
    {
        var intent = Assert.IsType<VolumeMenuIntent>(QueryRouter.Parse($"1 vol {token}", Three));
        Assert.Equal(expected, intent.ValueToken);
    }

    [Fact]
    public void VolumeTokensBeyondValue_AreIgnored()
    {
        var intent = Assert.IsType<VolumeMenuIntent>(QueryRouter.Parse("1 vol 30 extra junk", Three));
        Assert.Equal("30", intent.ValueToken);
    }

    [Fact]
    public void InputOnUnsupportedMonitor_FallsBackToMonitorMenu()
    {
        IReadOnlyList<MonitorSnapshot> monitors = [Monitor(1, "DELL U2723QE", input: false)];
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("1 in hdmi", monitors));
        Assert.Same(monitors[0], intent.Monitor);
    }

    [Fact]
    public void VolumeOnUnsupportedMonitor_FallsBackToMonitorMenu()
    {
        IReadOnlyList<MonitorSnapshot> monitors = [Monitor(1, "DELL U2723QE", volume: false)];
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("1 vol 30", monitors));
        Assert.Same(monitors[0], intent.Monitor);
    }

    [Theory]
    [InlineData("bri")]
    [InlineData("BRIGHTNESS")]
    [InlineData("bright")]
    [InlineData("b")]
    [InlineData("B")]
    public void BrightnessSynonyms_RouteToBrightnessMenu(string sub)
    {
        var intent = Assert.IsType<BrightnessMenuIntent>(QueryRouter.Parse($"3 {sub}", Three));
        Assert.Same(Three[2], intent.Monitor);
        Assert.Equal("", intent.ValueToken);
    }

    [Theory]
    [InlineData("70", "70")]
    [InlineData("150", "150")]
    [InlineData("abc", "abc")]
    public void BrightnessValueToken_PassesThroughUnvalidated(string token, string expected)
    {
        var intent = Assert.IsType<BrightnessMenuIntent>(QueryRouter.Parse($"1 bri {token}", Three));
        Assert.Equal(expected, intent.ValueToken);
    }

    [Fact]
    public void BrightnessOnUnsupportedMonitor_FallsBackToMonitorMenu()
    {
        IReadOnlyList<MonitorSnapshot> monitors = [Monitor(1, "DELL U2723QE", brightness: false)];
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("1 bri 70", monitors));
        Assert.Same(monitors[0], intent.Monitor);
    }

    [Theory]
    [InlineData("1 bogus")]
    [InlineData("1 invol")]
    [InlineData("1 lum 50")]
    public void UnknownSubToken_StaysOnMonitorMenu(string search)
    {
        var intent = Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse(search, Three));
        Assert.Same(Three[0], intent.Monitor);
    }

    [Fact]
    public void SurroundingAndInternalWhitespace_IsNormalizedByTokenizing()
    {
        var intent = Assert.IsType<InputMenuIntent>(QueryRouter.Parse("  1   in   hd   mi  ", Three));
        Assert.Same(Three[0], intent.Monitor);
        Assert.Equal("hd mi", intent.Filter);
    }

    [Theory]
    [InlineData("debug", "")]
    [InlineData("debug on", "on")]
    [InlineData("debug off", "off")]
    [InlineData("DEBUG On", "On")]
    [InlineData("  debug   off  ", "off")]
    public void DebugToken_RoutesToDiagnosticsView(string search, string expectedArgument)
    {
        var intent = Assert.IsType<DebugMenuIntent>(QueryRouter.Parse(search, Three));
        Assert.Equal(expectedArgument, intent.Argument);
    }

    [Fact]
    public void DebugToken_WinsOverAMonitorOfThatName()
    {
        // The support command must be reachable whatever the hardware is
        // called; that monitor stays addressable by its ordinal.
        IReadOnlyList<MonitorSnapshot> monitors = [Monitor(1, "Debug Panel HD")];

        Assert.IsType<DebugMenuIntent>(QueryRouter.Parse("debug", monitors));
        Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("1", monitors));
    }

    [Fact]
    public void DebugTokenDeeperInTheQuery_IsNotADiagnosticsRequest()
    {
        // Only the first token opens the view; "1 debug" is an unknown
        // sub-token on monitor 1 and must stay on its menu.
        Assert.IsType<MonitorMenuIntent>(QueryRouter.Parse("1 debug", Three));
    }
}
