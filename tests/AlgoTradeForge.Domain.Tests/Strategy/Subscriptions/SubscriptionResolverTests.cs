using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Subscriptions;

public class SubscriptionResolverTests
{
    private static Asset Btc() => TestAssets.BtcUsdt;
    private static Asset Eth() => CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2);

    [Fact]
    public void Resolve_AttachesAsset_WithoutMutatingWireFields()
    {
        var spec = new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var resolved = SubscriptionResolver.Resolve(spec, Btc());

        Assert.Equal("BTCUSDT", resolved.AssetName);
        Assert.Equal("binance", resolved.Exchange);
        Assert.Equal(DataFeedRole.Primary, resolved.Role);
        Assert.Equal("BTCUSDT", resolved.RequireAsset().Name);
    }

    [Fact]
    public void RequireAsset_Throws_WhenUnresolved()
    {
        var spec = new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        Assert.Throws<System.InvalidOperationException>(() => spec.RequireAsset());
    }

    [Theory]
    [InlineData("TimeBar", "ohlcv")]
    [InlineData("Tick", "ticks")]
    public void FeedKey_DerivesFromKind(string kind, string expected)
    {
        DataFeedSubscription spec = kind == "TimeBar"
            ? new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))
            : new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary);
        Assert.Equal(expected, spec.FeedKey());
    }

    [Fact]
    public void FeedKey_AltBar_IsFeedId()
    {
        var spec = new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_1m_500");
        Assert.Equal("EqV_1m_500", spec.FeedKey());
    }

    [Fact]
    public void FeedKey_Side_IsFeedId()
    {
        var spec = new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "OI_1m");
        Assert.Equal("OI_1m", spec.FeedKey());
    }

    [Fact]
    public void ResolveExecutionAsset_PrefersPrimary_EvenWhenNotIndex0()
    {
        var subs = new List<DataFeedSubscription>
        {
            SubscriptionResolver.Resolve(new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Side, TimeFrame.Parse("1h")), Eth()),
            SubscriptionResolver.Resolve(new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")), Btc()),
        };
        Assert.Equal("BTCUSDT", subs.ResolveExecutionAsset().Name);
    }

    [Fact]
    public void ResolveExecutionAsset_FallsBackToIndex0_WhenNoPrimary()
    {
        var subs = new List<DataFeedSubscription>
        {
            SubscriptionResolver.Resolve(new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Side, TimeFrame.Parse("1h")), Eth()),
            SubscriptionResolver.Resolve(new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Side, TimeFrame.Parse("1h")), Btc()),
        };
        Assert.Equal("ETHUSDT", subs.ResolveExecutionAsset().Name);
    }
}
