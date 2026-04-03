using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Filter;

public sealed class RegimeFilterModuleTests
{
    private static readonly Int64Bar DefaultBar = TestBars.Flat();

    [Fact]
    public void Evaluate_RegimeInAllowedSet_Returns100()
    {
        var context = new StrategyContext();
        context.CurrentRegime = MarketRegime.Trending;

        var filter = new RegimeFilterModule(context, MarketRegime.Trending);

        Assert.Equal(100, filter.Evaluate(DefaultBar, OrderSide.Buy));
    }

    [Fact]
    public void Evaluate_RegimeNotInAllowedSet_ReturnsNeg100()
    {
        var context = new StrategyContext();
        context.CurrentRegime = MarketRegime.RangeBound;

        var filter = new RegimeFilterModule(context, MarketRegime.Trending);

        Assert.Equal(-100, filter.Evaluate(DefaultBar, OrderSide.Buy));
    }

    [Fact]
    public void Evaluate_RegimeIsUnknown_ReturnsZero()
    {
        var context = new StrategyContext();
        context.CurrentRegime = MarketRegime.Unknown;

        var filter = new RegimeFilterModule(context, MarketRegime.Trending);

        Assert.Equal(0, filter.Evaluate(DefaultBar, OrderSide.Buy));
    }

    [Fact]
    public void Evaluate_MultipleAllowedRegimes_MatchesAny()
    {
        var context = new StrategyContext();
        context.CurrentRegime = MarketRegime.HighVolatility;

        var filter = new RegimeFilterModule(context,
            MarketRegime.Trending, MarketRegime.HighVolatility);

        Assert.Equal(100, filter.Evaluate(DefaultBar, OrderSide.Buy));
    }

    [Fact]
    public void Evaluate_MultipleAllowed_NoMatch_ReturnsNeg100()
    {
        var context = new StrategyContext();
        context.CurrentRegime = MarketRegime.RangeBound;

        var filter = new RegimeFilterModule(context,
            MarketRegime.Trending, MarketRegime.HighVolatility);

        Assert.Equal(-100, filter.Evaluate(DefaultBar, OrderSide.Buy));
    }

    [Fact]
    public void Evaluate_DirectionDoesNotAffectResult()
    {
        var context = new StrategyContext();
        context.CurrentRegime = MarketRegime.Trending;

        var filter = new RegimeFilterModule(context, MarketRegime.Trending);

        Assert.Equal(100, filter.Evaluate(DefaultBar, OrderSide.Buy));
        Assert.Equal(100, filter.Evaluate(DefaultBar, OrderSide.Sell));
    }
}
