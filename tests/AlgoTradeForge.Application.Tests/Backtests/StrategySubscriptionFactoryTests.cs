using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

/// <summary>
/// T14 — direct unit tests for the polymorphic <see cref="DataFeedSubscription"/> →
/// strategy-side <see cref="DataSubscription"/> bridge. Previously exercised only
/// transitively through BacktestPreparer integration tests; this file pins the
/// per-subtype mapping explicitly so a subtype regression surfaces here, not via a
/// confusing downstream loader failure.
/// </summary>
public sealed class StrategySubscriptionFactoryTests
{
    private static readonly AlgoTradeForge.Domain.Asset Asset = TestAssets.BtcUsdt;

    [Fact]
    public void FromPrimary_TimeBar_PassesThroughTimeFrame()
    {
        var sub = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("1h"));

        var ds = StrategySubscriptionFactory.FromPrimary(sub, Asset);

        Assert.Equal(TimeFrame.Parse("1h"), ds.TimeFrame);
        // FeedKey defaults to "ohlcv" for time-bar primaries — that's what BacktestPreparer
        // checks against when deciding the load path.
        Assert.Equal("ohlcv", ds.FeedKey);
    }

    [Fact]
    public void FromPrimary_AltBar_ResolvesSourceTimeFrameFromFeedId_AndCarriesFeedId()
    {
        var sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");

        var ds = StrategySubscriptionFactory.FromPrimary(sub, Asset);

        Assert.Equal(TimeFrame.Parse("1m"), ds.TimeFrame);   // source-code "1m" → 1m placeholder
        Assert.Equal("EqV_1m_500m", ds.FeedKey);
    }

    [Fact]
    public void FromPrimary_TickSourcedAltBar_FallsBackTo1mPlaceholder()
    {
        var sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_ticks_500m");

        var ds = StrategySubscriptionFactory.FromPrimary(sub, Asset);

        Assert.Equal(TimeFrame.Parse("1m"), ds.TimeFrame);   // canonical sentinel
        Assert.Equal("EqV_ticks_500m", ds.FeedKey);
    }

    [Fact]
    public void FromPrimary_Tick_UsesTicksFeedKeyAnd1mPlaceholder()
    {
        var sub = new TickSubscription("BTC", "ex", DataFeedRole.Primary);

        var ds = StrategySubscriptionFactory.FromPrimary(sub, Asset);

        Assert.Equal(TimeFrame.Parse("1m"), ds.TimeFrame);
        Assert.Equal("ticks", ds.FeedKey);
    }

    [Fact]
    public void FromPrimary_SideFeed_Throws()
    {
        var sub = new SideFeedSubscription("BTC", "ex", DataFeedRole.Side, "funding-rate");

        var ex = Assert.Throws<InvalidOperationException>(
            () => StrategySubscriptionFactory.FromPrimary(sub, Asset));
        Assert.Contains("SideFeedSubscription", ex.Message);
    }

    // -----------------------------------------------------------------------------
    // ResolveSourceTimeFrame — the alt-bar source-code → TimeFrame parser. Pinned
    // separately so the 3-component grammar guard is exercised directly.
    // -----------------------------------------------------------------------------

    [Theory]
    [InlineData("EqV_1m_500m", "1m")]
    [InlineData("EqT_5m_1k", "5m")]
    [InlineData("EqD_1h_2M", "1h")]
    [InlineData("EqI_15m_500", "15m")]
    public void ResolveSourceTimeFrame_TimeBarSourceCode_ReturnsParsedTimeFrame(
        string feedId, string expectedCode)
    {
        var tf = StrategySubscriptionFactory.ResolveSourceTimeFrame(feedId);
        Assert.Equal(TimeFrame.Parse(expectedCode), tf);
    }

    [Fact]
    public void ResolveSourceTimeFrame_TicksSourceCode_FallsBackTo1m()
    {
        var tf = StrategySubscriptionFactory.ResolveSourceTimeFrame("EqV_ticks_500m");
        Assert.Equal(TimeFrame.Parse("1m"), tf);
    }

    [Fact]
    public void ResolveSourceTimeFrame_NotThreeComponents_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => StrategySubscriptionFactory.ResolveSourceTimeFrame("invalid_id"));
        Assert.Contains("positional grammar", ex.Message);
    }

    [Fact]
    public void ResolveSourceTimeFrame_FourComponents_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => StrategySubscriptionFactory.ResolveSourceTimeFrame("Eq_V_1m_500m"));
        Assert.Contains("positional grammar", ex.Message);
    }

    [Fact]
    public void ResolveSourceTimeFrame_InvalidSourceCode_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => StrategySubscriptionFactory.ResolveSourceTimeFrame("EqV_garbage_500m"));
        Assert.Contains("source code", ex.Message);
    }
}
