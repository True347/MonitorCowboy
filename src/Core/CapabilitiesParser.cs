using System.Globalization;

namespace MonitorCowboy.Core;

/// <summary>
/// Tolerant parser for the DDC/CI capabilities string, e.g.
/// "(prot(monitor) type(lcd) ... vcp(02 10 14(05 08) 60(0F 11 1B) 62) ...)".
/// Only the vcp(...) section is read. Malformed input never throws: junk
/// tokens are skipped, unbalanced parentheses are handled best-effort, and
/// when a code appears more than once the last occurrence wins.
/// </summary>
public static class CapabilitiesParser
{
    private static readonly uint[] NoValues = [];

    /// <summary>Parses a raw capabilities string; null when it contains no vcp(...) section at all.</summary>
    public static ParsedCapabilities? Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        var i = FindVcpSectionStart(raw);
        if (i < 0)
            return null;

        var codes = new Dictionary<byte, IReadOnlyList<uint>>();
        var depth = 1;
        var tokenStart = -1;
        List<uint>? openValues = null;

        // Flushes the token ending at 'end'. Returns the parsed code when the
        // token was a valid VCP code at depth 1, so a '(' immediately after it
        // can open that code's supported-value list.
        byte? Flush(int end)
        {
            if (tokenStart < 0)
                return null;
            var token = raw[tokenStart..end];
            tokenStart = -1;
            if (depth == 1)
            {
                if (byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                {
                    codes[code] = NoValues;
                    return code;
                }
            }
            else if (depth == 2 && openValues is not null
                && uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                openValues.Add(value);
            }
            return null;
        }

        for (; i < raw.Length && depth > 0; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case '(':
                    var owner = Flush(i);
                    depth++;
                    if (owner is not null)
                    {
                        openValues = [];
                        codes[owner.Value] = openValues;
                    }
                    break;
                case ')':
                    Flush(i);
                    depth--;
                    if (depth < 2)
                        openValues = null;
                    break;
                default:
                    if (char.IsWhiteSpace(c))
                        Flush(i);
                    else if (tokenStart < 0)
                        tokenStart = i;
                    break;
            }
        }

        if (depth > 0)
            Flush(raw.Length);

        return new ParsedCapabilities(codes);
    }

    // Finds "vcp" case-insensitively as a standalone identifier whose next
    // non-whitespace character is '(' - never inside a longer word such as
    // "vcpname". Returns the index just past that '(', or -1 when absent.
    private static int FindVcpSectionStart(string raw)
    {
        for (var i = 0; i + 3 <= raw.Length; i++)
        {
            if (raw[i] is not ('v' or 'V') ||
                raw[i + 1] is not ('c' or 'C') ||
                raw[i + 2] is not ('p' or 'P'))
                continue;
            if (i > 0 && IsIdentifierChar(raw[i - 1]))
                continue;
            var j = i + 3;
            while (j < raw.Length && char.IsWhiteSpace(raw[j]))
                j++;
            if (j < raw.Length && raw[j] == '(')
                return j + 1;
        }
        return -1;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c == '_';
}
