using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams>(TParams parameters) : IInt64BarStrategy, IEventBusReceiver
    where TParams : StrategyParamsBase
{
    protected TParams Params { get; } = parameters;

    protected IEventBus EventBus { get; private set; } = NullEventBus.Instance;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public virtual void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnInit() { }

    public virtual void OnTrade(Fill fill, Order order) { }

    void IEventBusReceiver.SetEventBus(IEventBus bus) => EventBus = bus;
}
