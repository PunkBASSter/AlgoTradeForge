using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class CointegrationBreakExitRuleTests
{
    private static readonly Int64Bar DefaultBar = TestBars.Flat();

    private static OrderGroup CreateGroup() => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Buy,
        EntryQuantity = 1m,
        Asset = TestAssets.BtcUsdt,
    };

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var rule = new CointegrationBreakExitRule();
        Assert.Equal("CointegrationBreak", rule.Name);
    }

    [Fact]
    public void Evaluate_CointegratedTrue_ReturnsZero()
    {
        var context = new StrategyContext();
        context.Set("crossasset.cointegrated", true);

        var rule = new CointegrationBreakExitRule();
        Assert.Equal(0, rule.Evaluate(DefaultBar, context, CreateGroup()));
    }

    [Fact]
    public void Evaluate_CointegratedFalse_ReturnsNeg100()
    {
        var context = new StrategyContext();
        context.Set("crossasset.cointegrated", false);

        var rule = new CointegrationBreakExitRule();
        Assert.Equal(-100, rule.Evaluate(DefaultBar, context, CreateGroup()));
    }

    [Fact]
    public void Evaluate_NoCointegrationKey_ReturnsZero()
    {
        var context = new StrategyContext();

        var rule = new CointegrationBreakExitRule();
        Assert.Equal(0, rule.Evaluate(DefaultBar, context, CreateGroup()));
    }
}
