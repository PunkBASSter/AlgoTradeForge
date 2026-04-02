using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules;

public sealed class StrategyContextTests
{
    private static readonly DataSubscription DefaultSubscription =
        new(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1));

    private static readonly Int64Bar SampleBar =
        new(1_700_000_000_000L, 5000L, 5100L, 4900L, 5050L, 100L);

    [Fact]
    public void Update_PopulatesCurrentBar()
    {
        var ctx = new StrategyContext();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(SampleBar, ctx.CurrentBar);
    }

    [Fact]
    public void Update_PopulatesCurrentSubscription()
    {
        var ctx = new StrategyContext();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(DefaultSubscription, ctx.CurrentSubscription);
    }

    [Fact]
    public void Update_PopulatesCash()
    {
        var ctx = new StrategyContext();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(50_000L, ctx.Cash);
    }

    [Fact]
    public void Update_PopulatesEquityAsCashPlusUsedMargin()
    {
        var ctx = new StrategyContext();
        var orders = CreateOrderContext(cash: 50_000L, usedMargin: 10_000L);

        ctx.Update(SampleBar, DefaultSubscription, orders);

        Assert.Equal(60_000L, ctx.Equity);
    }

    [Fact]
    public void SetAndGet_StoresAndRetrievesTypedValue()
    {
        var ctx = new StrategyContext();

        ctx.Set("threshold", 42L);

        Assert.Equal(42L, ctx.Get<long>("threshold"));
    }

    [Fact]
    public void SetAndGet_StringValue()
    {
        var ctx = new StrategyContext();

        ctx.Set("label", "bullish");

        Assert.Equal("bullish", ctx.Get<string>("label"));
    }

    [Fact]
    public void SetAndGet_OverwritesPreviousValue()
    {
        var ctx = new StrategyContext();

        ctx.Set("count", 1);
        ctx.Set("count", 2);

        Assert.Equal(2, ctx.Get<int>("count"));
    }

    [Fact]
    public void Has_ReturnsTrueForSetKey()
    {
        var ctx = new StrategyContext();

        ctx.Set("exists", true);

        Assert.True(ctx.Has("exists"));
    }

    [Fact]
    public void Has_ReturnsFalseForMissingKey()
    {
        var ctx = new StrategyContext();

        Assert.False(ctx.Has("missing"));
    }

    [Fact]
    public void Get_ReturnsDefaultForMissingKey_ValueType()
    {
        var ctx = new StrategyContext();

        Assert.Equal(0L, ctx.Get<long>("missing"));
    }

    [Fact]
    public void Get_ReturnsDefaultForMissingKey_ReferenceType()
    {
        var ctx = new StrategyContext();

        Assert.Null(ctx.Get<string>("missing"));
    }

    [Fact]
    public void CurrentRegime_DefaultsToUnknown()
    {
        var ctx = new StrategyContext();

        Assert.Equal(MarketRegime.Unknown, ctx.CurrentRegime);
    }

    [Fact]
    public void CurrentAtr_DefaultsToZero()
    {
        var ctx = new StrategyContext();

        Assert.Equal(0L, ctx.CurrentAtr);
    }

    [Fact]
    public void CurrentVolatility_DefaultsToZero()
    {
        var ctx = new StrategyContext();

        Assert.Equal(0d, ctx.CurrentVolatility);
    }

    private static IOrderContext CreateOrderContext(long cash, long usedMargin)
    {
        var orders = Substitute.For<IOrderContext>();
        orders.Cash.Returns(cash);
        orders.UsedMargin.Returns(usedMargin);
        return orders;
    }
}
