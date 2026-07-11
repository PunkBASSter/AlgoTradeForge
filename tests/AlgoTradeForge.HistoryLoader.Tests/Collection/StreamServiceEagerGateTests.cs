using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

// Post-3a the eager/lazy decision is the group's collect value (materialized into the plan by
// LegacyGroupImporter / CollectionPlanBuilder). At the legacy config boundary the bridge maps
// Eager → "eager" and everything else → "on-demand"; streams gate on collect == "eager".
public sealed class StreamServiceEagerGateTests
{
    private static CollectionPlan Plan(string type, string feedName, string collect) =>
        new(
            type == AssetTypes.Spot
                ? [CollectionAssets.Spot("BTCUSDT", 2, CollectionAssets.Feed(feedName, "", collect))]
                : [CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(feedName, "", collect))],
            [], []);

    [Fact]
    public void BookTicker_Eager_Streams()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Plan(AssetTypes.Perpetual, FeedNames.BookTicker, "eager"), AssetTypes.IsFutures);
        Assert.Single(symbols);
    }

    [Fact]
    public void BookTicker_NonEager_DoesNotStream()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Plan(AssetTypes.Perpetual, FeedNames.BookTicker, "on-demand"), AssetTypes.IsFutures);
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
    public void SpotAggTrades_NonEager_DoesNotStream()
    {
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Plan(AssetTypes.Spot, FeedNames.Ticks, "on-demand"));
        Assert.Empty(symbols);
    }
}
