using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

// Streams are collect-if-declared, independent of the collect value. The eager/on-demand axis
// governs BACKFILL; streams have no backfill (live-only, no archive/REST), so declaring the feed
// IS the opt-in. A stream feed present in the plan streams regardless of eager/on-demand; a feed
// absent from the plan does not. Keeping streams off the eager flag also keeps them out of the
// DesiredStateService kick path (Collect == "eager" only), so gapped streams are never spuriously
// kicked for a backfill that cannot exist.
public sealed class StreamServiceEagerGateTests
{
    private static CollectionPlan Plan(string type, string feedName, string collect) =>
        new(
            type == AssetTypes.Spot
                ? [CollectionAssets.Spot("BTCUSDT", 2, CollectionAssets.Feed(feedName, "", collect))]
                : [CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(feedName, "", collect))],
            [], []);

    private static CollectionPlan PlanWithoutStream(string type) =>
        new(
            type == AssetTypes.Spot
                ? [CollectionAssets.Spot("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.Candles, "1m", "eager"))]
                : [CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.Candles, "1m", "eager"))],
            [], []);

    [Fact]
    public void BookTicker_Eager_Streams()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Plan(AssetTypes.Perpetual, FeedNames.BookTicker, "eager"), AssetTypes.IsFutures);
        Assert.Single(symbols);
    }

    [Fact]
    public void BookTicker_OnDemand_StillStreams()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Plan(AssetTypes.Perpetual, FeedNames.BookTicker, "on-demand"), AssetTypes.IsFutures);
        Assert.Single(symbols);
    }

    [Fact]
    public void BookTicker_NotDeclared_DoesNotStream()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            PlanWithoutStream(AssetTypes.Perpetual), AssetTypes.IsFutures);
        Assert.Empty(symbols);
    }

    [Fact]
    public void BookTicker_SpotEager_Streams()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Plan(AssetTypes.Spot, FeedNames.BookTicker, "eager"), AssetTypes.IsSpot);
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_Eager_Streams()
    {
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Plan(AssetTypes.Spot, FeedNames.Ticks, "eager"));
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_OnDemand_StillStreams()
    {
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Plan(AssetTypes.Spot, FeedNames.Ticks, "on-demand"));
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_NotDeclared_DoesNotStream()
    {
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            PlanWithoutStream(AssetTypes.Spot));
        Assert.Empty(symbols);
    }
}
