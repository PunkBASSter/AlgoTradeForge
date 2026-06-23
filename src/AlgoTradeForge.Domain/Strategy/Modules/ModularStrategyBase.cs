using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public abstract class ModularStrategyBase<TParams, TContext>(TParams parameters, IIndicatorFactory? indicators = null)
    : StrategyBase<TParams>(parameters, indicators)
    where TParams : ModularStrategyParamsBase
    where TContext : StrategyContextBase, new()
{
    private readonly List<IIndicator<Int64Bar, long>> _longIndicators = [];
    private readonly List<IIndicator<Int64Bar, double>> _doubleIndicators = [];
    private readonly Dictionary<int, List<Int64Bar>> _barHistories = [];

    protected TContext Context { get; private set; } = null!;

    protected void RegisterIndicator(IIndicator<Int64Bar, long> indicator) =>
        _longIndicators.Add(indicator);

    protected void RegisterIndicator(IIndicator<Int64Bar, double> indicator) =>
        _doubleIndicators.Add(indicator);

    // ── Lifecycle: sealed orchestration ──

    public sealed override void OnInit()
    {
        Context = new TContext();
        base.OnInit();
        OnStrategyInit();
    }

    protected sealed override void OnBarStartInner(Int64Bar bar, DataFeedSubscription subscription) { }

    protected sealed override void OnBarCompleteInner(Int64Bar bar, DataFeedSubscription subscription)
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

        Context.Update(bar, subscription, Orders);

        OnContextUpdated(bar, subscription);

        // ── PHASE 2: MANAGE POSITIONS ──
        ManagePositions(TradeRegistry, Context);

        // ── PHASE 3: EVALUATE ENTRY ──
        if (TradeRegistry.ActiveGroupCount < (Params.TradeRegistry.MaxConcurrentGroups == 0
                ? int.MaxValue : Params.TradeRegistry.MaxConcurrentGroups))
        {
            EvaluateEntry(bar, subscription);
        }
    }

    public sealed override void OnTrade(Fill fill, Order order)
    {
        base.OnTrade(fill, order);
        OnOrderFilled(fill, order);
    }

    // ── Virtual hooks ──

    protected virtual void OnStrategyInit() { }
    protected virtual void OnContextUpdated(Int64Bar bar, DataFeedSubscription sub) { }
    protected virtual void OnOrderFilled(Fill fill, Order order) { }

    protected virtual void ManagePositions(
        TradeRegistryModule tradeRegistry, TContext context) { }

    protected virtual void EvaluateEntry(Int64Bar bar, DataFeedSubscription sub) { }

    // ── Entry pipeline hooks (used by strategies that override EvaluateEntry) ──

    protected virtual (long price, OrderType type) GetEntryPrice(
        Int64Bar bar, OrderSide direction, TContext context)
        => (0, OrderType.Market);

    protected virtual (long stopLoss, TpLevel[] takeProfits) GetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, TContext context) => (0, []);

    protected virtual void CreateEntryGroup(
        Asset asset, OrderSide direction, OrderType orderType, long entryPrice,
        long stopLoss, TpLevel[] takeProfits, decimal quantity,
        TContext context)
    {
        TradeRegistry.OpenGroup(
            asset, direction, orderType, quantity, stopLoss,
            takeProfits,
            entryLimitPrice: orderType == OrderType.Limit ? entryPrice : null,
            entryStopPrice: orderType == OrderType.Stop ? entryPrice : null);
    }
}
