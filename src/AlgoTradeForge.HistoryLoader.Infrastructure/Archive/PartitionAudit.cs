namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

// Single streaming pass over a partition: total data lines and distinct-by-first-column (ts) count.
// Assumes rows are ts-sorted (both writers sort), so duplicates are adjacent — O(1) memory.
// public: consumed by the WebApi maintenance endpoint and the test project.
public static class PartitionAudit
{
    public static async Task<(long Lines, long Distinct)> Count(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return (0, 0);

        long lines = 0, distinct = 0;
        string? lastTs = null;
        using var reader = new StreamReader(path);
        await reader.ReadLineAsync(ct); // header
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            lines++;
            var comma = line.IndexOf(',');
            var ts = comma < 0 ? line : line[..comma];
            if (!string.Equals(ts, lastTs, StringComparison.Ordinal))
            {
                distinct++;
                lastTs = ts;
            }
        }
        return (lines, distinct);
    }
}
