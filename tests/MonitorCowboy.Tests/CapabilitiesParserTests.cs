using MonitorCowboy.Core;
using Xunit;

namespace MonitorCowboy.Tests;

public class CapabilitiesParserTests
{
    private const string DellCaps =
        "(prot(monitor) type(lcd) model(U2723QE) cmds(01 02 03 07 0C E3 F3) " +
        "vcp(02 04 05 08 10 12 14(05 08 0B 0C) 16 18 1A 60(0F 11 1B) 62 87 8D(01 02) " +
        "AC AE B2 B6 C6 C8 C9 CA CC(02 03 04 06 09 0A 0D 12 14 16 1E) D6(01 04 05) " +
        "DC(00 03 05) DF E0(00 01 02 03 04) F0(00 08) F1 F2)";

    private const string LgCaps =
        "(prot(monitor)type(lcd)model(27GL850)cmds(01 02 03 0C E3 F3)" +
        "vcp(02 04 05 06 08 10 12 14(05 08 0B) 16 18 1A 52 60(11 12 0F 10) AC AE B2 B6 " +
        "C6 C8 C9 CA CC(01 02 03 04 05 06 09 0A 0D 10 12 16) D6(01 04) DF 62 8D FF)" +
        "mswhql(1)asset_eep(40)mccs_ver(2.1))";

    [Fact]
    public void Parse_DellStyleString_ExtractsCodesAndValueLists()
    {
        var caps = CapabilitiesParser.Parse(DellCaps);

        Assert.NotNull(caps);
        Assert.Equal(new uint[] { 0x0F, 0x11, 0x1B }, caps.ValuesFor(Vcp.InputSource));
        Assert.True(caps.Supports(Vcp.AudioSpeakerVolume));
        Assert.Empty(caps.ValuesFor(Vcp.AudioSpeakerVolume));
        Assert.True(caps.Supports(0x10));
        Assert.Equal(new uint[] { 0x05, 0x08, 0x0B, 0x0C }, caps.ValuesFor(0x14));
        Assert.Equal(new uint[] { 0x00, 0x01, 0x02, 0x03, 0x04 }, caps.ValuesFor(0xE0));
        // 0x01 and 0xE3 appear only in the cmds(...) section, not in vcp(...).
        Assert.False(caps.Supports(0x01));
        Assert.False(caps.Supports(0xE3));
    }

    [Fact]
    public void Parse_LgStyleString_NoSpacesBetweenSections()
    {
        var caps = CapabilitiesParser.Parse(LgCaps);

        Assert.NotNull(caps);
        Assert.Equal(new uint[] { 0x11, 0x12, 0x0F, 0x10 }, caps.ValuesFor(Vcp.InputSource));
        Assert.True(caps.Supports(Vcp.AudioSpeakerVolume));
        Assert.Empty(caps.ValuesFor(Vcp.AudioSpeakerVolume));
        Assert.True(caps.Supports(0xFF));
        // Values from the sections after vcp(...) must not leak in:
        // mswhql(1) and asset_eep(40).
        Assert.False(caps.Supports(0x01));
        Assert.False(caps.Supports(0x40));
    }

    [Fact]
    public void Parse_UppercaseVcpKeyword_IsFound()
    {
        var caps = CapabilitiesParser.Parse("(prot(monitor) VCP(10 60(0F 11) 62))");

        Assert.NotNull(caps);
        Assert.True(caps.Supports(0x10));
        Assert.Equal(new uint[] { 0x0F, 0x11 }, caps.ValuesFor(0x60));
        Assert.True(caps.Supports(0x62));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(prot(monitor) type(lcd) cmds(01 02 03))")]
    [InlineData("(vcpname(stuff) mccs_ver(2.2))")] // "vcp" only inside a longer identifier
    public void Parse_NoVcpSection_ReturnsNull(string raw)
        => Assert.Null(CapabilitiesParser.Parse(raw));

    [Fact]
    public void Parse_EmptyVcpSection_ReturnsEmptyNonNull()
    {
        var caps = CapabilitiesParser.Parse("(prot(monitor) vcp())");

        Assert.NotNull(caps);
        Assert.Empty(caps.VcpCodes);
    }

    [Fact]
    public void Parse_UnbalancedParensMidList_BestEffortWithoutThrow()
    {
        var caps = CapabilitiesParser.Parse("(prot(monitor) vcp(02 60(0F 11");

        Assert.NotNull(caps);
        Assert.True(caps.Supports(0x02));
        Assert.Equal(new uint[] { 0x0F, 0x11 }, caps.ValuesFor(0x60));
    }

    [Fact]
    public void Parse_ValueListOpenedAtEndOfString_TruncatedWithoutThrow()
    {
        var caps = CapabilitiesParser.Parse("(prot(monitor) vcp(10 60(");

        Assert.NotNull(caps);
        Assert.True(caps.Supports(0x10));
        Assert.True(caps.Supports(0x60));
        Assert.Empty(caps.ValuesFor(0x60));
    }

    [Fact]
    public void Parse_ExtraClosingParen_EndsSectionEarly()
    {
        var caps = CapabilitiesParser.Parse("(prot(monitor) vcp(10)) 62)");

        Assert.NotNull(caps);
        Assert.True(caps.Supports(0x10));
        Assert.False(caps.Supports(0x62));
    }

    [Fact]
    public void Parse_NestedValueList_IgnoredGracefully()
    {
        var caps = CapabilitiesParser.Parse("(vcp(60(0F(01 02) 11) 62))");

        Assert.NotNull(caps);
        Assert.Equal(new uint[] { 0x0F, 0x11 }, caps.ValuesFor(0x60));
        Assert.True(caps.Supports(0x62));
    }

    [Fact]
    public void Parse_JunkTokens_AreSkipped()
    {
        var caps = CapabilitiesParser.Parse("(vcp(10 xyz 1FF 60(0F zz 11) 62))");

        Assert.NotNull(caps);
        Assert.True(caps.Supports(0x10));
        Assert.False(caps.Supports(0xFF)); // "1FF" does not fit a byte and is not partially read
        Assert.Equal(new uint[] { 0x0F, 0x11 }, caps.ValuesFor(0x60));
        Assert.True(caps.Supports(0x62));
    }

    [Fact]
    public void Parse_ValueWiderThanOneByte_ParsedAsUint()
    {
        var caps = CapabilitiesParser.Parse("(vcp(DC(00 03 1F90)))");

        Assert.NotNull(caps);
        Assert.Equal(new uint[] { 0x00, 0x03, 0x1F90 }, caps.ValuesFor(0xDC));
    }

    // Duplicate codes are documented as last-wins: the final occurrence
    // replaces any earlier one, value list included.
    [Fact]
    public void Parse_DuplicateCode_LastOccurrenceWins()
    {
        var caps = CapabilitiesParser.Parse("(vcp(60(0F) 10 60(11 12)))");

        Assert.NotNull(caps);
        Assert.Equal(new uint[] { 0x11, 0x12 }, caps.ValuesFor(0x60));
        Assert.True(caps.Supports(0x10));
    }
}
