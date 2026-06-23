using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy;

public class StrategyParamsBase
{
    public virtual IList<DataFeedSubscription> DataSubscriptions { get; init; } = [];
    public virtual int RequiredSubscriptionCount => 1;
    public virtual TradeRegistryParams TradeRegistry { get; init; } = new();
}
