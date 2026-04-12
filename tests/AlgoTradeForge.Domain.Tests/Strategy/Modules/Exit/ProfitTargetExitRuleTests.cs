using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class ProfitTargetExitRuleTests
{
    private static StrategyContext CreateContext(long currentAtr)
    {
        var ctx = new StrategyContext();
        ctx.CurrentAtr = currentAtr;
        return ctx;
    }

    private static OrderGroup CreateLongGroup(long entryPrice) => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Buy,
        EntryQuantity = 1m,
        EntryPrice = entryPrice,
        Asset = TestAssets.BtcUsdt,
    };

    private static OrderGroup CreateShortGroup(long entryPrice) => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Sell,
        EntryQuantity = 1m,
        EntryPrice = entryPrice,
        Asset = TestAssets.BtcUsdt,
    };

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 3.0);
        Assert.Equal("ProfitTarget", rule.Name);
    }

    [Fact]
    public void Evaluate_LongPnlBelowTarget_ReturnsZero()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 3.0);
        var group = CreateLongGroup(entryPrice: 50000);
        var context = CreateContext(currentAtr: 1000);

        // Current price 51000 → PnL = 1000, target = 3 * 1000 = 3000
        var bar = TestBars.AtPrice(51000);
        Assert.Equal(0, rule.Evaluate(bar, context, group));
    }

    [Fact]
    public void Evaluate_LongPnlAtTarget_ReturnsNeg60()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 3.0);
        var group = CreateLongGroup(entryPrice: 50000);
        var context = CreateContext(currentAtr: 1000);

        // Current price 53000 → PnL = 3000, target = 3000
        var bar = TestBars.AtPrice(53000);
        Assert.Equal(-60, rule.Evaluate(bar, context, group));
    }

    [Fact]
    public void Evaluate_LongPnlAboveTarget_ReturnsNeg60()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 2.0);
        var group = CreateLongGroup(entryPrice: 50000);
        var context = CreateContext(currentAtr: 500);

        // Current price 52000 → PnL = 2000, target = 2 * 500 = 1000
        var bar = TestBars.AtPrice(52000);
        Assert.Equal(-60, rule.Evaluate(bar, context, group));
    }

    [Fact]
    public void Evaluate_ShortPnlAtTarget_ReturnsNeg60()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 2.0);
        var group = CreateShortGroup(entryPrice: 50000);
        var context = CreateContext(currentAtr: 1000);

        // Short: PnL = entry - current → 50000 - 48000 = 2000, target = 2000
        var bar = TestBars.AtPrice(48000);
        Assert.Equal(-60, rule.Evaluate(bar, context, group));
    }

    [Fact]
    public void Evaluate_ShortPnlBelowTarget_ReturnsZero()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 3.0);
        var group = CreateShortGroup(entryPrice: 50000);
        var context = CreateContext(currentAtr: 1000);

        // Short: PnL = 50000 - 49000 = 1000, target = 3000
        var bar = TestBars.AtPrice(49000);
        Assert.Equal(0, rule.Evaluate(bar, context, group));
    }

    [Fact]
    public void Evaluate_ZeroAtr_ReturnsZero()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 2.0);
        var group = CreateLongGroup(entryPrice: 50000);
        var context = CreateContext(currentAtr: 0);

        var bar = TestBars.AtPrice(55000);
        Assert.Equal(0, rule.Evaluate(bar, context, group));
    }

    [Fact]
    public void Evaluate_ZeroEntryPrice_ReturnsZero()
    {
        var rule = new ProfitTargetExitRule(atrMultiple: 2.0);
        var group = CreateLongGroup(entryPrice: 0);
        var context = CreateContext(currentAtr: 1000);

        var bar = TestBars.AtPrice(55000);
        Assert.Equal(0, rule.Evaluate(bar, context, group));
    }
}
