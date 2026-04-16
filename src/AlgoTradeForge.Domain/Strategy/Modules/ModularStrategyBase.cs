using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public abstract class ModularStrategyBase<TParams, TContext>(TParams parameters, IIndicatorFactory? indicators = null)
    : StrategyBase<TParams>(parameters, indicators), ITradeRegistryProvider
    where TParams : ModularStrategyParamsBase
    where TContext : StrategyContextBase, new()
{
    private readonly List<IIndicator<Int64Bar, long>> _longIndicators = [];
    private readonly List<IIndicator<Int64Bar, double>> _doubleIndicators = [];
    private readonly Dictionary<int, List<Int64Bar>> _barHistories = [];
    private TradeRegistryModule _tradeRegistry = null!;

    protected TContext Context { get; private set; } = null!;

    protected void RegisterIndicator(IIndicator<Int64Bar, long> indicator) =>
        _longIndicators.Add(indicator);

    protected void RegisterIndicator(IIndicator<Int64Bar, double> indicator) =>
        _doubleIndicators.Add(indicator);

    TradeRegistryModule ITradeRegistryProvider.TradeRegistry => _tradeRegistry;

    // ── Lifecycle: sealed orchestration ──

    public sealed override void OnInit()
    {
        Context = new TContext();
        _tradeRegistry = new TradeRegistryModule(Params.TradeRegistry);

        if (_tradeRegistry is IEventBusReceiver busReceiver)
            busReceiver.SetEventBus(EventBus);

        _tradeRegistry.SetClock(() => Context.CurrentBar.Timestamp);

        OnStrategyInit();
    }

    public sealed override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        // ── PHASE 1: UPDATE CONTEXT ──
        // Track bar history and compute indicators
        var subIndex = DataSubscriptions.IndexOf(subscription);
        if (!_barHistories.TryGetValue(subIndex, out var history))
        {
            history = [];
            _barHistories[subIndex] = history;
        }
        history.Add(bar);

        // Compute all registered indicators against bar history
        foreach (var ind in _longIndicators)
            ind.Compute(history);
        foreach (var ind in _doubleIndicators)
            ind.Compute(history);

        Context.Update(bar, subscription, orders);

        OnContextUpdated(bar, subscription);

        // ── PHASE 2: MANAGE POSITIONS ──
        ManagePositions(_tradeRegistry, Context, orders);

        // ── PHASE 3: EVALUATE ENTRY ──
        if (_tradeRegistry.ActiveGroupCount < (Params.TradeRegistry.MaxConcurrentGroups == 0
                ? int.MaxValue : Params.TradeRegistry.MaxConcurrentGroups))
        {
            EvaluateEntry(bar, subscription, orders);
        }
    }

    public sealed override void OnTrade(Fill fill, Order order, IOrderContext orders)
    {
        _tradeRegistry.OnFill(fill, order, orders);
        OnOrderFilled(fill, order);
    }

    // ── Virtual hooks ──

    protected virtual void OnStrategyInit() { }
    protected virtual void OnContextUpdated(Int64Bar bar, DataSubscription sub) { }
    protected virtual void OnOrderFilled(Fill fill, Order order) { }

    protected virtual void ManagePositions(
        TradeRegistryModule tradeRegistry, TContext context, IOrderContext orders) { }

    protected virtual void EvaluateEntry(Int64Bar bar, DataSubscription sub, IOrderContext orders) { }

    // ── Entry pipeline hooks (used by strategies that override EvaluateEntry) ──

    /// <summary>
    /// Returns a signed signal score: positive = Buy, negative = Sell, 0 = no signal.
    /// Magnitude indicates conviction (e.g., +80 = Buy strength 80, -80 = Sell strength 80).
    /// The strategy is responsible for applying its own signal threshold filter.
    /// </summary>
    protected virtual int GenerateSignal(Int64Bar bar, TContext context) => 0;

    protected virtual (long price, OrderType type) GetEntryPrice(
        Int64Bar bar, OrderSide direction, TContext context)
        => (0, OrderType.Market);

    protected virtual (long stopLoss, TpLevel[] takeProfits) GetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, TContext context) => (0, []);

    protected virtual void CreateEntryGroup(
        Asset asset, OrderSide direction, OrderType orderType, long entryPrice,
        long stopLoss, TpLevel[] takeProfits, decimal quantity,
        TContext context, IOrderContext orders)
    {
        _tradeRegistry.OpenGroup(
            orders, asset, direction, orderType, quantity, stopLoss,
            takeProfits,
            entryLimitPrice: orderType == OrderType.Limit ? entryPrice : null,
            entryStopPrice: orderType == OrderType.Stop ? entryPrice : null);
    }
}
