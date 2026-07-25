using MonitorCowboy.Core;
using Xunit;

namespace MonitorCowboy.Tests;

public class VcpDefsTests
{
    [Theory]
    [InlineData(0x0Fu, "DisplayPort-1")]
    [InlineData(0x11u, "HDMI-1")]
    [InlineData(0x12u, "HDMI-2")]
    [InlineData(0x1Bu, "USB-C")]
    public void NameOf_KnownValues_ReturnsFriendlyName(uint value, string expected)
        => Assert.Equal(expected, InputSourceNames.NameOf(value));

    [Fact]
    public void NameOf_UnknownValue_RendersHex()
        => Assert.Equal("Input 0x25", InputSourceNames.NameOf(0x25));

    [Fact]
    public void NameOf_VendorFlaggedRead_FallsBackToLowByte()
        => Assert.Equal("HDMI-1", InputSourceNames.NameOf(0x0F11)); // high byte carries a vendor flag

    [Theory]
    [InlineData(0x0F11u, 0x11u, true)]   // vendor flag in high byte, same input
    [InlineData(0x11u, 0x11u, true)]
    [InlineData(0x11u, 0x12u, false)]
    public void SameInput_MasksHighBytes(uint read, uint target, bool expected)
        => Assert.Equal(expected, InputSourceNames.SameInput(read, target));
}
