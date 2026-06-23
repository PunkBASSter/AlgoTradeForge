using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules;

public sealed class StrategyContextTests
{
    private static readonly DataFeedSubscription DefaultSubscription =
        TestSubs.Of(TestAssets.BtcUsdt, new TimeFrame(TimeSpan.FromMinutes(1)));

    private static readonly Int64Bar SampleBar =
        new(1_700_000_000_000L, 5000L, 5100L, 4900L, 5050L, 100L);

    [Fact]
    public void Update_PopulatesCurrentBar()
    {
        var ctx = new StrategyContextBase();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(SampleBar, ctx.CurrentBar);
    }

    [Fact]
    public void Update_PopulatesCurrentSubscription()
    {
        var ctx = new StrategyContextBase();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(DefaultSubscription, ctx.CurrentSubscription);
    }

    [Fact]
    public void Update_PopulatesCash()
    {
        var ctx = new StrategyContextBase();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(50_000L, ctx.Cash);
    }

    [Fact]
    public void Update_PopulatesEquityAsCashPlusUsedMargin()
    {
        var ctx = new StrategyContextBase();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(60_000L, ctx.Equity);
    }

    private static IOrderContext CreateOrderContext(long cash, long usedMargin)
    {
        var orders = Substitute.For<IOrderContext>();
        orders.Cash.Returns(cash);
        orders.UsedMargin.Returns(usedMargin);
        return orders;
    }
}
