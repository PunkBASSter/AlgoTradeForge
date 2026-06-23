using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Domain.Indicators;

public interface IIndicatorFactory
{
    IIndicator<TInp, TBuff> Create<TInp, TBuff>(
        IIndicator<TInp, TBuff> indicator,
        DataFeedSubscription subscription);
}
