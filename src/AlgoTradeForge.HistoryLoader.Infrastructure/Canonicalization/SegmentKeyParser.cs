using System.Globalization;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public static class SegmentKeyParser
{
    public static bool TryParse(string key, string liveMdPrefix, out SegmentLocation loc)
    {
        loc = default;
        if (string.IsNullOrEmpty(key) || !key.EndsWith(".atft", StringComparison.Ordinal)) return false;

        var prefix = liveMdPrefix.TrimEnd('/') + "/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var parts = key[prefix.Length..].Split('/');
        if (parts.Length != 4) return false;

        var venue = parts[0];
        var instrumentOrVenue = parts[1];
        var stream = parts[2];
        var file = parts[3];

        var name = file[..^".atft".Length];
        var dash = name.IndexOf('-');
        if (dash <= 0) return false;

        if (!long.TryParse(name[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var createdAtMs)
            || !long.TryParse(name[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var firstSeq))
            return false;

        loc = new SegmentLocation(venue, instrumentOrVenue, stream, createdAtMs, firstSeq, key);
        return true;
    }
}
