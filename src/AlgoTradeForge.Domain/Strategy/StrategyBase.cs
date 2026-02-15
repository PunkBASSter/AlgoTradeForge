using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams> : IIntBarStrategy
    where TParams : StrategyParamsBase
{
    protected TParams Params { get; }

    protected StrategyBase(TParams parameters) => Params = parameters;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public abstract void OnBar(Int64Bar bar, DataSubscription subscription, IOrderContext orders);

    public virtual void OnTrade(Fill fill, Order order) { }
}
