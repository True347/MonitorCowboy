namespace MonitorCowboy.Core;

/// <summary>VCP feature codes used by this plugin (MCCS).</summary>
public static class Vcp
{
    public const byte InputSource = 0x60;
    public const byte AudioSpeakerVolume = 0x62;
}

/// <summary>
/// Friendly names for VCP 0x60 (Input Source) values.
/// 0x01-0x12 follow the MCCS 2.2 table; values above it are vendor-specific
/// (USB-C has no standard value; 0x1B is the value commonly used by Dell).
/// The authoritative list of accepted values is always the monitor's own
/// capabilities string, never this table.
/// </summary>
public static class InputSourceNames
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x01] = "VGA-1",
        [0x02] = "VGA-2",
        [0x03] = "DVI-1",
        [0x04] = "DVI-2",
        [0x05] = "Composite-1",
        [0x06] = "Composite-2",
        [0x07] = "S-Video-1",
        [0x08] = "S-Video-2",
        [0x09] = "Tuner-1",
        [0x0A] = "Tuner-2",
        [0x0B] = "Tuner-3",
        [0x0C] = "Component-1",
        [0x0D] = "Component-2",
        [0x0E] = "Component-3",
        [0x0F] = "DisplayPort-1",
        [0x10] = "DisplayPort-2",
        [0x11] = "HDMI-1",
        [0x12] = "HDMI-2",
        [0x1B] = "USB-C",
    };

    /// <summary>Friendly name for a raw 0x60 value, e.g. "HDMI-1". Unknown values render as "Input 0xNN".</summary>
    public static string NameOf(uint rawValue)
    {
        if (Names.TryGetValue(rawValue, out var name))
            return name;
        // Some monitors set vendor flags in the high bytes when reporting the
        // current input; retry the lookup with the low byte before giving up.
        var low = rawValue & 0xFF;
        if (low != rawValue && Names.TryGetValue(low, out var lowName))
            return lowName;
        return $"Input 0x{rawValue:X2}";
    }

    /// <summary>
    /// Compare a value read back from the monitor against a target value.
    /// Reads may carry vendor flags in the high bytes, so comparison masks
    /// to the low byte; writes must always use the raw capabilities value.
    /// </summary>
    public static bool SameInput(uint readValue, uint targetValue)
        => (readValue & 0xFF) == (targetValue & 0xFF);

    /// <summary>
    /// Generic input list offered when a monitor answers VCP but its
    /// capabilities string cannot be read (a common real-world failure —
    /// capabilities is the most fragile DDC/CI command). Wrong entries are
    /// harmless: the write simply comes back unverified.
    /// </summary>
    public static readonly IReadOnlyList<uint> CommonProbeValues =
        [0x0F, 0x10, 0x11, 0x12, 0x1B, 0x03, 0x04, 0x01];
}
