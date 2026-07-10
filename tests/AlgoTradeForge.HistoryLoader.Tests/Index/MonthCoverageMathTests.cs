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
}
