using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class MonthCoverageMathTests
{
    private static long Ms(int y, int m, int d = 1) =>
        new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void FullPastMonth_ExactRowCount_IsCovered()
    {
        // Jan 2024, 1h → 744 expected rows.
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 744,
            gaps: [], completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 743,
            gaps: [], completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
    }

    [Fact]
    public void GapCredit_CoversMissingRows()
    {
        // 24h hole: gap ends are present rows → 23 creditable slots; 744 - 23 = 721 actual needed.
        var gaps = new[] { new DataGap { FromMs = Ms(2024, 1, 10), ToMs = Ms(2024, 1, 11) } };
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 721,
            gaps, completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 720,
            gaps, completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
    }

    [Fact]
    public void CurrentMonth_ExpectationClampedToNow()
    {
        // now = Jan 2 2024 00:00 → 24 expected 1h rows.
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 24,
            gaps: [], completeMonths: null, effectiveStartMs: null, nowMs: Ms(2024, 1, 2)));
    }

    [Fact]
    public void MonthlyCompletenessFeeds_UseMarkerOnly()
    {
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Ticks, "", 2024, 1, actualRows: 0,
            gaps: [], completeMonths: ["2024-01"], effectiveStartMs: null, nowMs: Ms(2025, 1)));
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Ticks, "", 2024, 1, actualRows: 999,
            gaps: [], completeMonths: [], effectiveStartMs: null, nowMs: Ms(2025, 1)));
    }

    [Fact]
    public void ListingClamp_OnlyInsideMonth()
    {
        Assert.Equal(Ms(2024, 1, 15), MonthCoverageMath.ListingClamp(Ms(2024, 1, 15), 2024, 1));
        Assert.Null(MonthCoverageMath.ListingClamp(Ms(2023, 12, 15), 2024, 1));
        Assert.Null(MonthCoverageMath.ListingClamp(null, 2024, 1));
    }

    // -------------------------------------------------------------------------
    // Migrated from MonthCoverageCalculatorTests: these asserted MonthCoverageMath
    // arithmetic through a written partition file. Fix B removed the content read, so
    // they now call IsCovered directly. nowMs pinned to 2026-07-07 (the old fixture clock).
    // -------------------------------------------------------------------------

    [Fact]
    public void GapCrossingMonthBoundary_CreditsClampedSlot()
    {
        // April 2024 (720 hourly slots). Gap 2024-03-31 23:00 → 2024-04-01 05:00 leaves 5 missing
        // April slots; 715 present rows + 5 gap credit = 720 → covered. Old formula lost the
        // slot AT the clamp boundary (2024-04-01 00:00), credited 4, kept it uncovered forever.
        var gapFrom = new DateTimeOffset(2024, 3, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var gapTo = new DateTimeOffset(2024, 4, 1, 5, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var covered = MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 4, actualRows: 715,
            gaps: [new DataGap { FromMs = gapFrom, ToMs = gapTo }],
            completeMonths: null, effectiveStartMs: null, nowMs: Ms(2026, 7, 7));

        Assert.True(covered);
    }

    [Fact]
    public void MonthEntirelyInsideGap_NoPartition_Covered()
    {
        // April 2024 lies entirely inside one gap (2024-03-31 23:00 → 2024-05-01 03:00).
        // No partition rows; the gap credit alone must cover the month.
        var gapFrom = new DateTimeOffset(2024, 3, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var gapTo = new DateTimeOffset(2024, 5, 1, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var covered = MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 4, actualRows: 0,
            gaps: [new DataGap { FromMs = gapFrom, ToMs = gapTo }],
            completeMonths: null, effectiveStartMs: null, nowMs: Ms(2026, 7, 7));

        Assert.True(covered);
    }

    [Fact]
    public void ListingMonth_CoveredFromFirstDataTimestamp()
    {
        // March 2024: source data starts March 15 (408 hours to month end). effectiveStartMs
        // clamps the expectation to 408; 408 rows → covered.
        var covered = MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 3, actualRows: 408,
            gaps: [], completeMonths: null, effectiveStartMs: Ms(2024, 3, 15), nowMs: Ms(2026, 7, 7));

        Assert.True(covered);
    }

    [Fact]
    public void EffectiveStart_AtMonthStart_HoleLater_StillUncovered()
    {
        // effectiveStartMs == monthStart: clamp is a no-op, full 744-row expectation applies.
        // 734 rows, no gap credit → not covered.
        var covered = MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 3, actualRows: 734,
            gaps: [], completeMonths: null, effectiveStartMs: Ms(2024, 3, 1), nowMs: Ms(2026, 7, 7));

        Assert.False(covered);
    }

    [Fact]
    public void EffectiveStart_ClampsListingMonth_ButHoleyMonthStaysUncovered()
    {
        // Direction 1 (listing month): data starts March 10; 22 days × 24 = 528 rows to month
        // end; effectiveStartMs clamps expectation to 528 → covered.
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 3, actualRows: 528,
            gaps: [], completeMonths: null, effectiveStartMs: Ms(2024, 3, 10), nowMs: Ms(2026, 7, 7)));

        // Direction 2 (genuinely holey): first data at month start, 700 of 744 rows, no gap
        // credit → still uncovered.
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 3, actualRows: 700,
            gaps: [], completeMonths: null, effectiveStartMs: Ms(2024, 3, 1), nowMs: Ms(2026, 7, 7)));
    }

    [Fact]
    public void IntervalFeed_Unaffected_ByCompleteMonthsParam()
    {
        // completeMonths is ignored for interval feeds; the row-count predicate still governs.
        var covered = MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 744,
            gaps: [], completeMonths: [], effectiveStartMs: null, nowMs: Ms(2026, 7, 7));

        Assert.True(covered);
    }
}
