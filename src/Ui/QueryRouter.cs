using MonitorCowboy.Core;

namespace MonitorCowboy.Ui;

/// <summary>
/// Parses the query text after the action keyword into a <see cref="QueryIntent"/>.
/// Pure function over the snapshot list - no I/O, no value validation (the value
/// token is passed through raw; <c>ResultFactory</c> validates at render time).
/// </summary>
public static class QueryRouter
{
    public static QueryIntent Parse(string search, IReadOnlyList<MonitorSnapshot> monitors)
    {
        var tokens = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new MonitorListIntent("");

        var monitor = ResolveMonitor(tokens[0], monitors);
        if (monitor is null)
            return new MonitorListIntent(search.Trim());

        if (tokens.Length == 1)
            return new MonitorMenuIntent(monitor);

        if (IsSynonym(tokens[1], "in", "input"))
            return monitor.SupportsInput
                ? new InputMenuIntent(monitor, string.Join(' ', tokens.Skip(2)))
                : new MonitorMenuIntent(monitor);

        if (IsSynonym(tokens[1], "vol", "volume", "v"))
            return monitor.SupportsVolume
                ? new VolumeMenuIntent(monitor, tokens.Length > 2 ? tokens[2] : "")
                : new MonitorMenuIntent(monitor);

        // Unknown sub-token: stay on the monitor's menu instead of erroring out.
        return new MonitorMenuIntent(monitor);
    }

    private static MonitorSnapshot? ResolveMonitor(string token, IReadOnlyList<MonitorSnapshot> monitors)
    {
        // A digits-only token is always an ordinal, never a name match; out of
        // range (or unparsable) falls back to the L1 list.
        if (token.All(char.IsAsciiDigit))
            return int.TryParse(token, out var n) && n >= 1 && n <= monitors.Count
                ? monitors[n - 1]
                : null;

        MonitorSnapshot? match = null;
        foreach (var candidate in monitors)
        {
            if (!candidate.FriendlyName.Contains(token, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
                return null;
            match = candidate;
        }
        return match;
    }

    private static bool IsSynonym(string token, params string[] synonyms)
        => synonyms.Any(s => token.Equals(s, StringComparison.OrdinalIgnoreCase));
}
