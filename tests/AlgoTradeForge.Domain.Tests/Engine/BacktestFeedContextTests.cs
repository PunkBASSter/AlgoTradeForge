using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BacktestFeedContextTests
{
    private static (BacktestFeedContext ctx, DataFeedSchema schema) CreateFundingContext()
    {
        var schema = new DataFeedSchema("funding", ["fundingRate", "markPrice"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));

        // 3 funding records at 8h intervals (timestamps in ms)
        var series = new FeedSeries(
            [28_800_000L, 57_600_000L, 86_400_000L],
            [
                [0.0001, 0.0002, -0.0001],  // fundingRate
                [50000.0, 50100.0, 49900.0], // markPrice
            ]);

        var ctx = new BacktestFeedContext();
        ctx.Register("funding", schema, series);
        return (ctx, schema);
    }

    #region TryGetLatest

    [Fact]
    public void TryGetLatest_BeforeAnyAdvance_ReturnsFalse()
    {
        var (ctx, _) = CreateFundingContext();

        Assert.False(ctx.TryGetLatest("funding", out _));
    }

    [Fact]
    public void TryGetLatest_AfterAdvancePastFirstRecord_ReturnsTrue()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(28_800_000L);

        Assert.True(ctx.TryGetLatest("funding", out var values));
        Assert.Equal(0.0001, values[0]); // fundingRate
        Assert.Equal(50000.0, values[1]); // markPrice
    }

    [Fact]
    public void TryGetLatest_UnknownFeed_ReturnsFalse()
    {
        var (ctx, _) = CreateFundingContext();

        Assert.False(ctx.TryGetLatest("nonexistent", out _));
    }

    [Fact]
    public void TryGetLatest_BetweenRecords_ReturnsLastConsumed()
    {
        var (ctx, _) = CreateFundingContext();

        // Advance to 40_000_000 — past first record (28.8M) but before second (57.6M)
        ctx.AdvanceTo(40_000_000L);

        Assert.True(ctx.TryGetLatest("funding", out var values));
        Assert.Equal(0.0001, values[0]); // first record's fundingRate
    }

    [Fact]
    public void TryGetLatest_AdvancePastMultipleRecords_ReturnsLatest()
    {
        var (ctx, _) = CreateFundingContext();

        // Advance past all 3 records at once
        ctx.AdvanceTo(100_000_000L);

        Assert.True(ctx.TryGetLatest("funding", out var values));
        Assert.Equal(-0.0001, values[0]); // third (last) record's fundingRate
    }

    #endregion

    #region HasNewData

    [Fact]
    public void HasNewData_NoAdvance_ReturnsFalse()
    {
        var (ctx, _) = CreateFundingContext();

        Assert.False(ctx.HasNewData("funding"));
    }

    [Fact]
    public void HasNewData_RecordConsumed_ReturnsTrue()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(28_800_000L);

        Assert.True(ctx.HasNewData("funding"));
    }

    [Fact]
    public void HasNewData_NoNewRecordThisStep_ReturnsFalse()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(28_800_000L);
        // Advance again but no new record between 28.8M and 40M
        ctx.AdvanceTo(40_000_000L);

        Assert.False(ctx.HasNewData("funding"));
    }

    [Fact]
    public void HasNewData_SecondRecordConsumed_ReturnsTrue()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(28_800_000L);
        ctx.AdvanceTo(57_600_000L);

        Assert.True(ctx.HasNewData("funding"));
    }

    #endregion

    #region GetSchema

    [Fact]
    public void GetSchema_RegisteredFeed_ReturnsSchema()
    {
        var (ctx, schema) = CreateFundingContext();

        var retrieved = ctx.GetSchema("funding");

        Assert.Same(schema, retrieved);
        Assert.Equal(0, retrieved.GetColumnIndex("fundingRate"));
        Assert.Equal(1, retrieved.GetColumnIndex("markPrice"));
    }

    [Fact]
    public void GetSchema_UnknownFeed_Throws()
    {
        var (ctx, _) = CreateFundingContext();

        Assert.Throws<InvalidOperationException>(() => ctx.GetSchema("nonexistent"));
    }

    #endregion

    #region Reset

    [Fact]
    public void Reset_ClearsCursorsAndState()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(100_000_000L);
        Assert.True(ctx.HasNewData("funding"));

        ctx.Reset();

        Assert.False(ctx.HasNewData("funding"));
        Assert.False(ctx.TryGetLatest("funding", out _));
    }

    [Fact]
    public void Reset_AllowsReplayFromStart()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(100_000_000L);
        ctx.Reset();

        // Re-advance to first record
        ctx.AdvanceTo(28_800_000L);

        Assert.True(ctx.TryGetLatest("funding", out var values));
        Assert.Equal(0.0001, values[0]);
    }

    #endregion

    #region GetAutoApplyFeeds

    [Fact]
    public void GetAutoApplyFeeds_OnlyReturnsAutoApplyFeedsWithNewData()
    {
        var fundingSchema = new DataFeedSchema("funding", ["fundingRate"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));
        var oiSchema = new DataFeedSchema("oi", ["sumOI"]); // no auto-apply

        var ctx = new BacktestFeedContext();
        ctx.Register("funding", fundingSchema, new FeedSeries([100L], [[0.0001]]));
        ctx.Register("oi", oiSchema, new FeedSeries([100L], [[1000.0]]));

        ctx.AdvanceTo(100L);

        var autoFeeds = ctx.GetAutoApplyFeeds().ToList();
        Assert.Single(autoFeeds);
        Assert.Equal("funding", autoFeeds[0].FeedKey);
    }

    [Fact]
    public void GetAutoApplyFeeds_NoNewData_ReturnsEmpty()
    {
        var (ctx, _) = CreateFundingContext();

        var autoFeeds = ctx.GetAutoApplyFeeds().ToList();
        Assert.Empty(autoFeeds);
    }

    #endregion

    #region Multiple feeds

    [Fact]
    public void MultipleFeeds_IndependentCursors()
    {
        var ctx = new BacktestFeedContext();

        ctx.Register("funding",
            new DataFeedSchema("funding", ["rate"]),
            new FeedSeries([100L, 300L], [[0.01, 0.02]]));

        ctx.Register("oi",
            new DataFeedSchema("oi", ["sumOI"]),
            new FeedSeries([200L, 400L], [[1000.0, 2000.0]]));

        // At t=150: only funding has data
        ctx.AdvanceTo(150L);
        Assert.True(ctx.HasNewData("funding"));
        Assert.False(ctx.HasNewData("oi"));

        // At t=250: only OI has new data
        ctx.AdvanceTo(250L);
        Assert.False(ctx.HasNewData("funding"));
        Assert.True(ctx.HasNewData("oi"));

        // Both should still have latest values
        Assert.True(ctx.TryGetLatest("funding", out var fundingVals));
        Assert.Equal(0.01, fundingVals[0]);
        Assert.True(ctx.TryGetLatest("oi", out var oiVals));
        Assert.Equal(1000.0, oiVals[0]);
    }

    #endregion

    #region Zero-allocation verification

    [Fact]
    public void TryGetLatest_ReturnsSameBufferInstance()
    {
        var (ctx, _) = CreateFundingContext();
        ctx.AdvanceTo(28_800_000L);

        ctx.TryGetLatest("funding", out var values1);
        ctx.AdvanceTo(57_600_000L);
        ctx.TryGetLatest("funding", out var values2);

        // Same buffer reused (zero-alloc design)
        Assert.Same(values1, values2);
    }

    #endregion
}
