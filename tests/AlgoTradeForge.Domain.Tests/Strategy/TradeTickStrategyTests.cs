using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

public class TradeTickStrategyTests
{
    private sealed class BarOnlyStrategy : IInt64BarStrategy
    {
        public string Version => "1";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = new List<DataFeedSubscription>();
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
    }

    private sealed class TickStrategy : IInt64BarStrategy, ITradeTickStrategy
    {
        public string Version => "1";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = new List<DataFeedSubscription>();
        public TradeTick? Last;
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
        public void OnTradeTick(in TradeTick tick, DataFeedSubscription subscription) => Last = tick;
    }

    [Fact]
    public void Bar_only_strategy_is_not_a_trade_tick_strategy()
    {
        IInt64BarStrategy s = new BarOnlyStrategy();
        Assert.False(s is ITradeTickStrategy);
    }

    [Fact]
    public void Implementing_ITradeTickStrategy_exposes_OnTradeTick()
    {
        var s = new TickStrategy();
        Assert.True(s is ITradeTickStrategy);

        var tick = new TradeTick(1, 100, 5, 7, AggressorSide.Buy);
        ((ITradeTickStrategy)s).OnTradeTick(in tick, TestSubs.Of(null!, default));
        Assert.Equal(7, s.Last!.Value.Sequence);
    }
}
