using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

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

    private static ArchiveMaterializerRegistry FuturesBookTickerRegistry()
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns("binance");
        m.FeedName.Returns(FeedNames.BookTicker);
        m.Supports(Arg.Any<string>()).Returns(ci => AssetTypes.IsFutures(ci.Arg<string>()));
        return new ArchiveMaterializerRegistry([m]);
    }

    [Fact]
    public void BookTicker_NoMaterializerToday_AlwaysStreams()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([]));
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: false), AssetTypes.IsFutures, policy);
        Assert.Single(symbols);
    }

    [Fact]
    public void BookTicker_FuturesReplenishable_StreamsOnlyWhenEager()
    {
        var policy = new CollectionPolicy(FuturesBookTickerRegistry());
        Assert.Empty(BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: false), AssetTypes.IsFutures, policy));
        Assert.Single(BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: true), AssetTypes.IsFutures, policy));
    }

    [Fact]
    public void BookTicker_SpotIrreplaceable_AlwaysStreams()
    {
        // The futures-only materializer must not silence the spot stream (spec §1).
        var policy = new CollectionPolicy(FuturesBookTickerRegistry());
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("spot", FeedNames.BookTicker, eager: false), AssetTypes.IsSpot, policy);
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_NoMaterializerToday_AlwaysStreams()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([]));
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Config("spot", FeedNames.Ticks, eager: false), policy);
        Assert.Single(symbols);
    }
}
