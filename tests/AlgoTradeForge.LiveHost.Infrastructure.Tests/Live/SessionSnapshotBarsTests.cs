using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class SessionSnapshotBarsTests
{
    private static readonly Asset Btc = CryptoAsset.Create("BTCUSDT", "Binance", 2);
    private static readonly TimeFrame OneMin = TimeFrame.Parse("1m");

    private static Int64Bar Bar(long ts, long close) => new(ts, close, close, close, close, 1);

    [Fact]
    public void Build_populates_bars_and_last_bar_for_a_time_bar_subscription()
    {
        var raw = new DataFeedSubscription[]
        {
            new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, OneMin),
        };
        var resolved = new[] { TestSubs.Of(Btc, OneMin) };
        var bars = new[] { Bar(1000, 100), Bar(2000, 110) };

        var result = SessionSnapshotBars.Build(resolved, (_, _) => bars);

        Assert.Equal(2, result.Bars.Count);
        Assert.Equal(110, result.Bars[^1].Close);
        var last = Assert.Single(result.LastBarsPerSubscription);
        Assert.Equal(2000, last.Bar.TimestampMs);
        Assert.Same(resolved[0], last.Subscription);
    }

    [Fact]
    public void Build_skips_tick_subscriptions_and_empty_sources()
    {
        var subs = new[] { TestSubs.Of(Btc, default, FeedKey: "tick") };

        var result = SessionSnapshotBars.Build(subs, (_, _) => throw new Exception("tick must not query"));

        Assert.Empty(result.Bars);
        Assert.Empty(result.LastBarsPerSubscription);
    }

    [Fact]
    public void Build_flat_bars_come_from_primary_subscription_zero()
    {
        var resolved = new[]
        {
            TestSubs.Of(Btc, OneMin),
            TestSubs.Of(Btc, default, FeedKey: "EqV_1m_500"),
        };

        var primaryBars = new[] { Bar(1000, 100) };
        var altBars = new[] { Bar(1500, 200), Bar(1600, 210) };

        var result = SessionSnapshotBars.Build(resolved, (_, spec) =>
            spec == BarSpecKey.TimeBar(OneMin) ? primaryBars : altBars);

        // Flat Bars = subscription[0] (the primary time-bar), not the alt feed.
        var bar = Assert.Single(result.Bars);
        Assert.Equal(100, bar.Close);
        // But last-bar exists for BOTH bar subscriptions.
        Assert.Equal(2, result.LastBarsPerSubscription.Count);
    }

}
