using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveMaterializerRegistryTests
{
    private static IArchiveMaterializer Stub(string exchange, string feed, bool futuresOnly = false)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns(exchange);
        m.FeedName.Returns(feed);
        m.Supports(Arg.Any<string>()).Returns(ci => !futuresOnly || AssetTypes.IsFutures(ci.Arg<string>()));
        return m;
    }

    [Fact]
    public void Resolve_ReturnsMaterializer_ForRegisteredTuple()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.NotNull(registry.Resolve("binance", FeedNames.Candles, AssetTypes.Spot));
        Assert.True(registry.IsReplenishable("binance", FeedNames.Candles, AssetTypes.Perpetual));
    }

    [Fact]
    public void UnknownExchange_IsIrreplaceableByConstruction()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.False(registry.IsReplenishable("ib", FeedNames.Candles, AssetTypes.Equity));
    }

    [Fact]
    public void AssetTypeSensitivity_FuturesOnlyMaterializer_RejectsSpot()
    {
        var registry = new ArchiveMaterializerRegistry(
            [Stub("binance", FeedNames.OpenInterest, futuresOnly: true)]);
        Assert.True(registry.IsReplenishable("binance", FeedNames.OpenInterest, AssetTypes.Perpetual));
        Assert.False(registry.IsReplenishable("binance", FeedNames.OpenInterest, AssetTypes.Spot));
    }

    [Fact]
    public void UnregisteredFeed_IsIrreplaceable()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.False(registry.IsReplenishable("binance", FeedNames.Liquidations, AssetTypes.Perpetual));
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnExchange()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.NotNull(registry.Resolve("Binance", FeedNames.Candles, AssetTypes.Spot));
    }
}
