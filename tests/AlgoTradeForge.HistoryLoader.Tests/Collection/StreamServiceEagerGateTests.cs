using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

// Post-3a the eager/lazy decision is the group's collect value (materialized into the plan by
// LegacyGroupImporter / CollectionPlanBuilder). At the legacy config boundary the bridge maps
// Eager → "eager" and everything else → "on-demand"; streams gate on collect == "eager".
public sealed class StreamServiceEagerGateTests
{
    private static HistoryLoaderOptions Config(string type, string feedName, bool eager) => new()
    {
        Assets =
        [
            new AssetCollectionConfig
            {
                Symbol = "BTCUSDT", Type = type,
                Feeds = [new FeedCollectionConfig { Name = feedName, Eager = eager }],
            },
        ],
    };

    [Fact]
    public void BookTicker_Eager_Streams()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: true), AssetTypes.IsFutures);
        Assert.Single(symbols);
    }

    [Fact]
    public void BookTicker_NonEager_DoesNotStream()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: false), AssetTypes.IsFutures);
        Assert.Empty(symbols);
    }

    [Fact]
    public void BookTicker_SpotEager_Streams()
    {
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("spot", FeedNames.BookTicker, eager: true), AssetTypes.IsSpot);
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_Eager_Streams()
    {
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Config("spot", FeedNames.Ticks, eager: true));
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_NonEager_DoesNotStream()
    {
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Config("spot", FeedNames.Ticks, eager: false));
        Assert.Empty(symbols);
    }
}
