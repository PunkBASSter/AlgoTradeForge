using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public interface IMonthCoverageCalculator
{
    // Interval feeds only in phase 1 (ticks' CompleteMonths marker is phase 3).
    // Covered iff partition exists AND actualRows + gapRows >= expectedRows for the month
    // (expected clamped to "now" for the current month — which therefore is never covered
    // unless now is past month end). gaps = recorded source DataGaps from FeedStatus.
    // effectiveStartMs: when the feed's first data row falls inside this month (listing month),
    // pass it to clamp the expectation start — the pre-listing hole has no present row before
    // it and is therefore unrecordable as a DataGap.
    Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        long? effectiveStartMs = null,
        CancellationToken ct = default);
}
