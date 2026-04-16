using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams>(TParams parameters, IIndicatorFactory? indicators = null)
    : IInt64BarStrategy, IEventBusReceiver, IFeedContextReceiver, ITradeRegistryProvider
    where TParams : StrategyParamsBase
{
    private TradeRegistryModule _tradeRegistry = new(parameters.TradeRegistry);

    public abstract string Version { get; }

    protected TParams Params { get; } = parameters;

    protected IEventBus EventBus { get; private set; } = NullEventBus.Instance;

    protected IFeedContext Feeds { get; private set; } = NullFeedContext.Instance;

    protected IIndicatorFactory Indicators { get; } = indicators ?? PassthroughIndicatorFactory.Instance;

    protected TradeRegistryModule TradeRegistry => _tradeRegistry;

    TradeRegistryModule ITradeRegistryProvider.TradeRegistry => _tradeRegistry;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public virtual void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }

    public virtual void OnInit()
    {
        if (_tradeRegistry is IEventBusReceiver busReceiver)
            busReceiver.SetEventBus(EventBus);
    }

    public virtual void OnTrade(Fill fill, Order order, IOrderContext orders)
    {
        _tradeRegistry.OnFill(fill, order, orders);
    }

    protected void EmitSignal(DateTimeOffset timestamp, string signalName, string assetName,
        string direction, decimal strength, string? reason = null)
    {
        EventBus.Emit(new SignalEvent(
            timestamp, GetType().Name,
            signalName, assetName, direction, strength, reason));
    }

    void IEventBusReceiver.SetEventBus(IEventBus bus) => EventBus = bus;

    void IFeedContextReceiver.SetFeedContext(IFeedContext context) => Feeds = context;
}
