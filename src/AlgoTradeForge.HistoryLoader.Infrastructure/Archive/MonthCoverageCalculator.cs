using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class MonthCoverageCalculator(TimeProvider clock) : IMonthCoverageCalculator
{
    public Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        MonthPartitionRow? indexedMonth,
        IReadOnlyList<string>? completeMonths = null,
        long? effectiveStartMs = null,
        CancellationToken ct = default)
    {
        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();

        // Interval-less feeds (ticks, funding-rate) are covered by the CompleteMonths marker only;
        // they have no month_partitions rows and must never touch the file/index-count branch.
        if (FeedNames.UsesMonthlyCompleteness(feedName))
            return Task.FromResult(MonthCoverageMath.IsCovered(
                feedName, interval, year, month, actualRows: 0, gaps, completeMonths, effectiveStartMs, nowMs));

        // Trust the index's row count when present — no content read.
        if (indexedMonth is { } row)
            return Task.FromResult(MonthCoverageMath.IsCovered(
                feedName, interval, year, month, row.Rows, gaps, completeMonths, effectiveStartMs, nowMs));

        // No index row. If the partition is on disk, the index is merely stale/cold: defer this month
        // (report covered) rather than re-download it — a rescan will populate the real count. Only a
        // genuinely absent file is uncovered.
        var partitionPath = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{interval}.csv");
        if (File.Exists(partitionPath))
            return Task.FromResult(true);

        return Task.FromResult(MonthCoverageMath.IsCovered(
            feedName, interval, year, month, actualRows: 0, gaps, completeMonths, effectiveStartMs, nowMs));
    }
}
