using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams>(TParams parameters) : IInt64BarStrategy
    where TParams : StrategyParamsBase
{
    protected TParams Params { get; } = parameters;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public virtual void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnInit() { }

    public virtual void OnTrade(Fill fill, Order order) { }
}
