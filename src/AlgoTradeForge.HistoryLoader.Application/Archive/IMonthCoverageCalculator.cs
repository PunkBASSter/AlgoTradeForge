using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public interface IMonthCoverageCalculator
{
    // For interval-less feeds (FeedNames.UsesMonthlyCompleteness), covered iff completeMonths
    // contains "{year:D4}-{month:D2}" — IntervalParser is never reached for these feeds.
    // For interval feeds: covered iff partition exists AND actualRows + gapRows >= expectedRows
    // (expected clamped to "now"; current month is never covered).
    // effectiveStartMs: pass the feed's first data row when it falls inside this month to clamp
    // the expectation start (pre-listing hole is unrecordable as a DataGap).
    Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string>? completeMonths = null,
        long? effectiveStartMs = null,
        CancellationToken ct = default);
}
