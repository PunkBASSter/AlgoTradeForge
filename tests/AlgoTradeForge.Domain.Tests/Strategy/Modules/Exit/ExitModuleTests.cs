using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class ExitModuleTests
{
    private static readonly Int64Bar DefaultBar = TestBars.Flat();
    private static readonly StrategyContext DefaultContext = new();

    private static OrderGroup CreateOrderGroup() => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Buy,
        EntryQuantity = 1m,
        Asset = TestAssets.BtcUsdt,
    };

    private static IExitRule CreateRule(int score)
    {
        var rule = Substitute.For<IExitRule>();
        rule.Evaluate(Arg.Any<Int64Bar>(), Arg.Any<StrategyContext>(), Arg.Any<OrderGroup>())
            .Returns(score);
        return rule;
    }

    [Fact]
    public void Evaluate_NoRules_ReturnsZero()
    {
        var module = new ExitModule();

        var result = module.Evaluate(DefaultBar, DefaultContext, CreateOrderGroup());

        Assert.Equal(0, result);
    }

    [Fact]
    public void Evaluate_SingleRuleReturningNegative_ReturnsThatScore()
    {
        var module = new ExitModule();
        module.AddRule(CreateRule(-50));

        var result = module.Evaluate(DefaultBar, DefaultContext, CreateOrderGroup());

        Assert.Equal(-50, result);
    }

    [Fact]
    public void Evaluate_MultipleRules_ReturnsMostNegative()
    {
        var module = new ExitModule();
        module.AddRule(CreateRule(-30));
        module.AddRule(CreateRule(-80));

        var result = module.Evaluate(DefaultBar, DefaultContext, CreateOrderGroup());

        Assert.Equal(-80, result);
    }

    [Fact]
    public void Evaluate_PositiveHoldAndNegativeExit_ReturnsNegative()
    {
        var module = new ExitModule();
        module.AddRule(CreateRule(50));
        module.AddRule(CreateRule(-100));

        var result = module.Evaluate(DefaultBar, DefaultContext, CreateOrderGroup());

        Assert.Equal(-100, result);
    }

    [Fact]
    public void Evaluate_AllRulesReturnZero_ReturnsZero()
    {
        var module = new ExitModule();
        module.AddRule(CreateRule(0));
        module.AddRule(CreateRule(0));

        var result = module.Evaluate(DefaultBar, DefaultContext, CreateOrderGroup());

        Assert.Equal(0, result);
    }
}
