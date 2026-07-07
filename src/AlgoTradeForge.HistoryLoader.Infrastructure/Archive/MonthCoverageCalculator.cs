using System.Collections.Concurrent;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class MonthCoverageCalculator : IMonthCoverageCalculator
{
    private readonly TimeProvider _clock;

    // Row counts memoized per (path, length, mtime). Partitions are replaced atomically
    // (PartitionFileWriter) or appended (BufferedPartitionWriter) — both move length+mtime,
    // so a stale entry cannot survive a content change.
    // TODO: no eviction — entries for deleted partitions persist; cap or prune if catalog-scale uptime makes this matter.
    private readonly ConcurrentDictionary<string, (long Length, DateTime MtimeUtc, long Rows)> _rowCounts = new();

    public MonthCoverageCalculator(TimeProvider clock) => _clock = clock;

    public async Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string>? completeMonths = null,
        long? effectiveStartMs = null,
        CancellationToken ct = default)
    {
        if (FeedNames.UsesMonthlyCompleteness(feedName))
            return completeMonths?.Contains($"{year:D4}-{month:D2}") ?? false;

        var intervalMs = (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        var monthStartMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var nextMonthDate = month == 12
            ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEndMs = nextMonthDate.ToUnixTimeMilliseconds();

        // Listing-month clamp: the pre-listing hole has no present row before it, so it is
        // unrecordable as a DataGap — the expectation starts at the feed's first data row.
        var effectiveStart = Math.Max(monthStartMs, effectiveStartMs ?? monthStartMs);

        var effectiveEndMs = Math.Min(monthEndMs, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (effectiveEndMs <= effectiveStart)
            return false;

        var expectedRows = (effectiveEndMs - effectiveStart) / intervalMs;

        // A month lying entirely inside one recorded gap has no partition file — correctly so.
        // Missing file means 0 actual rows; gap credit alone may still cover the month.
        var partitionPath = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{interval}.csv");
        long actualRows = 0;
        var fileInfo = new FileInfo(partitionPath);
        if (fileInfo.Exists)
            actualRows = await CountDataRows(fileInfo, ct);

        long gapRows = 0;
        foreach (var gap in gaps)
        {
            // Count missing slots strictly inside [effectiveStart, effectiveEndMs): the gap's
            // ends are present rows, so the first missing slot is FromMs + interval. When the
            // clamp clips (gap crosses the month edge), the slot AT the clamp boundary is
            // itself missing and must be counted — hence no blanket "− 1".
            var from = Math.Max(gap.FromMs + intervalMs, effectiveStart);
            var to = Math.Min(gap.ToMs, effectiveEndMs);
            var credit = (to - from) / intervalMs;
            if (credit > 0)
                gapRows += credit;
        }

        return actualRows + gapRows >= expectedRows;
    }

    private async Task<long> CountDataRows(FileInfo file, CancellationToken ct)
    {
        if (_rowCounts.TryGetValue(file.FullName, out var cached)
            && cached.Length == file.Length && cached.MtimeUtc == file.LastWriteTimeUtc)
            return cached.Rows;

        long lines = 0;
        using var reader = new StreamReader(file.FullName);
        while (await reader.ReadLineAsync(ct) is not null)
            lines++;

        var rows = Math.Max(0, lines - 1);
        _rowCounts[file.FullName] = (file.Length, file.LastWriteTimeUtc, rows);
        return rows;
    }
}
