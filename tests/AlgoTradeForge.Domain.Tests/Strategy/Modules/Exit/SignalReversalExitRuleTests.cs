using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class SignalReversalExitRuleTests
{
    private static readonly StrategyContext DefaultContext = new();
    private static readonly Int64Bar DefaultBar = TestBars.Flat();

    private static OrderGroup CreateGroup(OrderSide side) => new()
    {
        GroupId = 1,
        EntrySide = side,
        EntryQuantity = 1m,
        Asset = TestAssets.BtcUsdt,
    };

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var rule = new SignalReversalExitRule((_, _) => (0, OrderSide.Buy));
        Assert.Equal("SignalReversal", rule.Name);
    }

    [Fact]
    public void Evaluate_SignalFlippedFromBuyToSell_ReturnsNeg70()
    {
        // Entry was Buy, signal now says Sell
        var rule = new SignalReversalExitRule((_, _) => (50, OrderSide.Sell));
        var group = CreateGroup(OrderSide.Buy);

        Assert.Equal(-70, rule.Evaluate(DefaultBar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_SignalFlippedFromSellToBuy_ReturnsNeg70()
    {
        // Entry was Sell, signal now says Buy
        var rule = new SignalReversalExitRule((_, _) => (50, OrderSide.Buy));
        var group = CreateGroup(OrderSide.Sell);

        Assert.Equal(-70, rule.Evaluate(DefaultBar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_SignalSameDirectionAsBuy_ReturnsZero()
    {
        // Entry was Buy, signal still says Buy
        var rule = new SignalReversalExitRule((_, _) => (50, OrderSide.Buy));
        var group = CreateGroup(OrderSide.Buy);

        Assert.Equal(0, rule.Evaluate(DefaultBar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_SignalSameDirectionAsSell_ReturnsZero()
    {
        // Entry was Sell, signal still says Sell
        var rule = new SignalReversalExitRule((_, _) => (50, OrderSide.Sell));
        var group = CreateGroup(OrderSide.Sell);

        Assert.Equal(0, rule.Evaluate(DefaultBar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_ZeroSignalStrength_ReturnsZero()
    {
        // Signal strength is 0 → no signal → no reversal
        var rule = new SignalReversalExitRule((_, _) => (0, OrderSide.Sell));
        var group = CreateGroup(OrderSide.Buy);

        Assert.Equal(0, rule.Evaluate(DefaultBar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_DelegateReceivesBarAndContext()
    {
        Int64Bar? capturedBar = null;
        StrategyContext? capturedCtx = null;

        var rule = new SignalReversalExitRule((bar, ctx) =>
        {
            capturedBar = bar;
            capturedCtx = ctx;
            return (0, OrderSide.Buy);
        });

        var testBar = TestBars.Bullish();
        rule.Evaluate(testBar, DefaultContext, CreateGroup(OrderSide.Buy));

        Assert.Equal(testBar, capturedBar);
        Assert.Same(DefaultContext, capturedCtx);
    }
}
