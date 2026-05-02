using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Subscriptions;

/// <summary>
/// P4-11 — Pins the bridge between the polymorphic <see cref="DataFeedSubscription"/>
/// hierarchy (TRD §9.2) and the flat <see cref="DataFeedKind"/> enum used at the loader
/// boundary (TRD §9.5). The mapping is exhaustive — adding a fifth subscription kind
/// MUST update both the enum and the switch.
/// </summary>
public class DataFeedSubscriptionExtensionsTests
{
    [Fact]
    public void KindOf_TimeBarSubscription_ReturnsTimeBar()
    {
        DataFeedSubscription sub = new TimeBarSubscription(
            "BTC", "ex", DataFeedRole.Primary, new TimeFrame(TimeSpan.FromMinutes(1)));
        Assert.Equal(DataFeedKind.TimeBar, sub.KindOf());
    }

    [Fact]
    public void KindOf_AltBarSubscription_ReturnsAltBar()
    {
        DataFeedSubscription sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");
        Assert.Equal(DataFeedKind.AltBar, sub.KindOf());
    }

    [Fact]
    public void KindOf_TickSubscription_ReturnsTick()
    {
        DataFeedSubscription sub = new TickSubscription("BTC", "ex", DataFeedRole.Primary);
        Assert.Equal(DataFeedKind.Tick, sub.KindOf());
    }

    [Fact]
    public void KindOf_SideFeedSubscription_ReturnsSide()
    {
        DataFeedSubscription sub = new SideFeedSubscription("BTC", "ex", DataFeedRole.Side, "funding-rate");
        Assert.Equal(DataFeedKind.Side, sub.KindOf());
    }
}
