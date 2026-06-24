using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Collection;

public class CollectionCoverageTests
{
    private static readonly DataFeedSubscription[] Collected =
    [
        new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
        new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
        new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "funding-rate"),
    ];

    [Fact]
    public void Null_when_tick_collected()
    {
        var required = new[] { new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary) };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void Reports_when_tick_not_collected()
    {
        var required = new[] { new TickSubscription("ETHUSDT", "binance", DataFeedRole.Primary) };
        var unmet = CollectionCoverage.FindUnmet(Collected, required);
        Assert.NotNull(unmet);
        Assert.Contains("ETHUSDT", unmet);
    }

    [Fact]
    public void Null_when_timebar_interval_matches()
    {
        var required = new[] { new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")) };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void Reports_when_timebar_interval_differs()
    {
        var required = new[] { new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")) };
        Assert.NotNull(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void Null_when_sidefeed_collected()
    {
        var required = new[] { new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "funding-rate") };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void AltBar_validates_against_tick_root()
    {
        // EqV_ticks_1000 derives from the collected Tick root -> satisfied.
        var required = new[] { new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_ticks_1000") };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void AltBar_validates_against_candle_root()
    {
        // EqV_1m_1000 derives from the collected 1m candle root -> satisfied.
        var required = new[] { new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_1m_1000") };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));

        // EqV_5m_1000 needs a 5m candle root we did not collect -> reported.
        var missing = new[] { new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_5m_1000") };
        Assert.NotNull(CollectionCoverage.FindUnmet(Collected, missing));
    }
}
