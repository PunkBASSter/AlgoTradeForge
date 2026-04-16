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
        var ctx = new TestStrategyContext { IsCointegrated = true };
        var rule = new CointegrationBreakExitRule(ctx);
        Assert.Equal("CointegrationBreak", rule.Name);
    }

    [Fact]
    public void Evaluate_CointegratedTrue_ReturnsZero()
    {
        var ctx = new TestStrategyContext { IsCointegrated = true };
        var rule = new CointegrationBreakExitRule(ctx);

        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_CointegratedFalse_ReturnsNeg100()
    {
        var ctx = new TestStrategyContext { IsCointegrated = false };
        var rule = new CointegrationBreakExitRule(ctx);

        Assert.Equal(-100, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_DefaultCointegration_ReturnsNeg100()
    {
        // Default IsCointegrated is false
        var ctx = new TestStrategyContext();
        var rule = new CointegrationBreakExitRule(ctx);

        Assert.Equal(-100, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }
}
