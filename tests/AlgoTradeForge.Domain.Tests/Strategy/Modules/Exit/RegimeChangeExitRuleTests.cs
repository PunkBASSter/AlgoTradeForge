using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class RegimeChangeExitRuleTests
{
    private static readonly Int64Bar DefaultBar = TestBars.Flat();
    private static readonly StrategyContextBase BaseContext = new();

    private static OrderGroup CreateGroup() => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Buy,
        EntryQuantity = 1m,
        Asset = TestAssets.BtcUsdt,
    };

    private static TestStrategyContext CreateRegimeContext(MarketRegime regime) =>
        new() { CurrentRegime = regime };

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var ctx = CreateRegimeContext(MarketRegime.Unknown);
        var rule = new RegimeChangeExitRule(ctx);
        Assert.Equal("RegimeChange", rule.Name);
    }

    [Fact]
    public void Evaluate_SameRegimeAsEntry_ReturnsZero()
    {
        var ctx = CreateRegimeContext(MarketRegime.Trending);
        var rule = new RegimeChangeExitRule(ctx);

        // Activate with entry regime
        rule.Activate(1, MarketRegime.Trending);

        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_RegimeChanged_ReturnsNeg80()
    {
        var ctx = CreateRegimeContext(MarketRegime.RangeBound);
        var rule = new RegimeChangeExitRule(ctx);

        // Entered in Trending regime, now RangeBound
        rule.Activate(1, MarketRegime.Trending);

        Assert.Equal(-80, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_CurrentRegimeIsUnknown_ReturnsZero()
    {
        var ctx = CreateRegimeContext(MarketRegime.Unknown);
        var rule = new RegimeChangeExitRule(ctx);

        rule.Activate(1, MarketRegime.Trending);

        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_EntryRegimeWasUnknown_CurrentTrending_ReturnsZero()
    {
        var ctx = CreateRegimeContext(MarketRegime.Trending);
        var rule = new RegimeChangeExitRule(ctx);

        // Unknown at entry → transitions should not trigger exit
        rule.Activate(1, MarketRegime.Unknown);

        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_NotActivatedForGroup_ReturnsZero()
    {
        var ctx = CreateRegimeContext(MarketRegime.RangeBound);
        var rule = new RegimeChangeExitRule(ctx);

        // Never called Activate for group 1
        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Activate_TracksMultipleGroups()
    {
        var ctx = CreateRegimeContext(MarketRegime.Trending);
        var rule = new RegimeChangeExitRule(ctx);

        rule.Activate(1, MarketRegime.Trending);
        rule.Activate(2, MarketRegime.RangeBound);

        var group1 = new OrderGroup
        {
            GroupId = 1, EntrySide = OrderSide.Buy, EntryQuantity = 1m, Asset = TestAssets.BtcUsdt,
        };
        var group2 = new OrderGroup
        {
            GroupId = 2, EntrySide = OrderSide.Sell, EntryQuantity = 1m, Asset = TestAssets.BtcUsdt,
        };

        // Group 1 entered Trending → current Trending → no change
        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, group1));

        // Group 2 entered RangeBound → current Trending → changed!
        Assert.Equal(-80, rule.Evaluate(DefaultBar, ctx, group2));
    }

    [Fact]
    public void Remove_CleansUpGroupState()
    {
        var ctx = CreateRegimeContext(MarketRegime.RangeBound);
        var rule = new RegimeChangeExitRule(ctx);
        rule.Activate(1, MarketRegime.Trending);
        rule.Remove(1);

        // After removal, should return 0 (not activated)
        Assert.Equal(0, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }

    [Fact]
    public void Evaluate_TrendingToHighVolatility_ReturnsNeg80()
    {
        var ctx = CreateRegimeContext(MarketRegime.HighVolatility);
        var rule = new RegimeChangeExitRule(ctx);
        rule.Activate(1, MarketRegime.Trending);

        Assert.Equal(-80, rule.Evaluate(DefaultBar, ctx, CreateGroup()));
    }
}
