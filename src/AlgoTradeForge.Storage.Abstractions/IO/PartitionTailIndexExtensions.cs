using System.Globalization;

namespace AlgoTradeForge.Storage;

public static class PartitionTailIndexExtensions
{
    public static async Task<long?> GetLastTimestamp(this IPartitionTailIndex tail, string key, CancellationToken ct = default)
    {
        var line = await tail.GetLastLine(key, ct);
        if (line is null) return null;

        var comma = line.IndexOf(',');
        var field = comma < 0 ? line : line[..comma];
        return long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ? ts : null;
    }
}
