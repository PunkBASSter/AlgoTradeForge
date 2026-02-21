using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Domain.Indicators;

public interface IIndicatorFactory
{
    IIndicator<TInp, TBuff> Create<TInp, TBuff>(
        IIndicator<TInp, TBuff> indicator,
        DataSubscription subscription);
}
