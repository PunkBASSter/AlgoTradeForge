using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public interface IStrategy
{
    string Version { get; }
    void OnInit();
    void OnTrade(Fill fill, Order order);
    IList<DataFeedSubscription> DataSubscriptions { get; }
}
