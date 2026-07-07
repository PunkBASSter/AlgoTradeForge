using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class CollectionPolicyTests
{
    private static IArchiveMaterializer Materializer(string exchange, string feed, bool supportsSpot = true)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns(exchange);
        m.FeedName.Returns(feed);
        m.Supports(Arg.Any<string>()).Returns(ci =>
            supportsSpot || AlgoTradeForge.HistoryLoader.Domain.AssetTypes.IsFutures(ci.Arg<string>()));
        return m;
    }

    private static AssetCollectionConfig Asset(string type = "perpetual") =>
        new() { Symbol = "BTCUSDT", Type = type };

    [Fact]
    public void ReplenishableFeed_WithoutOverride_IsLazy()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([Materializer("binance", "candles")]));
        Assert.False(policy.IsEagerlyCollected(Asset(), new FeedCollectionConfig { Name = "candles", Interval = "1h" }));
    }

    [Fact]
    public void ReplenishableFeed_WithEagerTrue_IsEager()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([Materializer("binance", "candles")]));
        Assert.True(policy.IsEagerlyCollected(Asset(),
            new FeedCollectionConfig { Name = "candles", Interval = "1h", Eager = true }));
    }

    [Fact]
    public void IrreplaceableFeed_IsAlwaysEager()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([]));
        Assert.True(policy.IsEagerlyCollected(Asset(), new FeedCollectionConfig { Name = "liquidations" }));
    }

    [Fact]
    public void AssetTypeSensitivity_FuturesOnlyMaterializer_LeavesSpotEager()
    {
        var registry = new ArchiveMaterializerRegistry([Materializer("binance", "book-ticker", supportsSpot: false)]);
        var policy = new CollectionPolicy(registry);
        Assert.True(policy.IsEagerlyCollected(Asset("spot"), new FeedCollectionConfig { Name = "book-ticker" }));
        Assert.False(policy.IsEagerlyCollected(Asset("perpetual"), new FeedCollectionConfig { Name = "book-ticker" }));
    }

    [Fact]
    public void UnknownExchange_IsEager()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([Materializer("binance", "candles")]));
        var asset = new AssetCollectionConfig { Symbol = "AAPL", Exchange = "ib", Type = "equity" };
        Assert.True(policy.IsEagerlyCollected(asset, new FeedCollectionConfig { Name = "candles" }));
    }
}
