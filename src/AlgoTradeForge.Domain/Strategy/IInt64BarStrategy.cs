using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

public interface IInt64BarStrategy : IStrategy
{
    void OnBarStart(Int64Bar bar, DataFeedSubscription subscription) { }
    void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription);
}
