namespace MonitorCowboy.Core;

/// <summary>
/// Result of parsing a monitor capabilities string, e.g.
/// "(prot(monitor) type(lcd) ... vcp(02 04 10 60(0F 11 12) 62 ...) ...)".
/// Keys are the supported VCP codes; the value list holds the accepted values
/// for non-continuous codes (empty for continuous codes such as 0x62).
/// </summary>
public sealed record ParsedCapabilities(IReadOnlyDictionary<byte, IReadOnlyList<uint>> VcpCodes)
{
    public bool Supports(byte code) => VcpCodes.ContainsKey(code);

    public IReadOnlyList<uint> ValuesFor(byte code)
        => VcpCodes.TryGetValue(code, out var values) ? values : [];
}
