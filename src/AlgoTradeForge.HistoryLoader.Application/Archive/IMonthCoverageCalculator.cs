using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public interface IMonthCoverageCalculator
{
    // Interval feeds only in phase 1 (ticks' CompleteMonths marker is phase 3).
    // Covered iff partition exists AND actualRows + gapRows >= expectedRows for the month
    // (expected clamped to "now" for the current month — which therefore is never covered
    // unless now is past month end). gaps = recorded source DataGaps from FeedStatus.
    Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        CancellationToken ct = default);
}
