using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams>(TParams parameters, IIndicatorFactory? indicators = null) : IInt64BarStrategy, IEventBusReceiver
    where TParams : StrategyParamsBase
{
    protected TParams Params { get; } = parameters;

    protected IEventBus EventBus { get; private set; } = NullEventBus.Instance;

    protected IIndicatorFactory Indicators { get; } = indicators ?? PassthroughIndicatorFactory.Instance;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public virtual void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnInit() { }

    public virtual void OnTrade(Fill fill, Order order) { }

    protected void EmitSignal(DateTimeOffset timestamp, string signalName, string assetName,
        string direction, decimal strength, string? reason = null)
    {
        EventBus.Emit(new SignalEvent(
            timestamp, GetType().Name,
            signalName, assetName, direction, strength, reason));
    }

    void IEventBusReceiver.SetEventBus(IEventBus bus) => EventBus = bus;
}
