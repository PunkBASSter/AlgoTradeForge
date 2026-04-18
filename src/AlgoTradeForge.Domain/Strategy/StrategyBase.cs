using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams>(TParams parameters, IIndicatorFactory? indicators = null)
    : IInt64BarStrategy, IEventBusReceiver, IFeedContextReceiver, IOrderContextReceiver, ITradeRegistryProvider
    where TParams : StrategyParamsBase
{
    private readonly TradeRegistryModule _tradeRegistry = new(parameters.TradeRegistry);

    public abstract string Version { get; }

    protected TParams Params { get; } = parameters;

    protected IEventBus EventBus { get; private set; } = NullEventBus.Instance;

    protected IFeedContext Feeds { get; private set; } = NullFeedContext.Instance;

    protected IIndicatorFactory Indicators { get; } = indicators ?? PassthroughIndicatorFactory.Instance;

    protected TradeRegistryModule TradeRegistry => _tradeRegistry;

    protected IOrderContext Orders { get; private set; } = UninitializedOrderContext.Instance;

    TradeRegistryModule ITradeRegistryProvider.TradeRegistry => _tradeRegistry;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public virtual void OnBarStart(Int64Bar bar, DataSubscription subscription) { }

    public virtual void OnBarComplete(Int64Bar bar, DataSubscription subscription) { }

    public virtual void OnInit()
    {
        if (_tradeRegistry is IEventBusReceiver busReceiver)
            busReceiver.SetEventBus(EventBus);
    }

    public virtual void OnTrade(Fill fill, Order order)
    {
        _tradeRegistry.OnFill(fill, order);
    }

    protected void EmitSignal(DateTimeOffset timestamp, string signalName, string assetName,
        string direction, decimal strength, string? reason = null)
    {
        EventBus.Emit(new SignalEvent(
            timestamp, GetType().Name,
            signalName, assetName, direction, strength, reason));
    }

    void IOrderContextReceiver.SetOrderContext(IOrderContext context)
    {
        Orders = context;
        _tradeRegistry.SetOrderContext(context);
    }

    void IEventBusReceiver.SetEventBus(IEventBus bus) => EventBus = bus;

    void IFeedContextReceiver.SetFeedContext(IFeedContext context) => Feeds = context;
}
