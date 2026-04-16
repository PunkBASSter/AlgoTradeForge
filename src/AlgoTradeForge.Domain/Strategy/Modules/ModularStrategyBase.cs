using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
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
    private IMoneyManagementModule _moneyManagement = null!;

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
        _moneyManagement = Params.MoneyManagement;

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

        // Phases 2-3 only on primary subscription
        var isPrimary = DataSubscriptions.Count == 0 ||
                        ReferenceEquals(subscription, DataSubscriptions[0]);
        if (!isPrimary) return;

        // ── PHASE 2: MANAGE POSITIONS ──
        if (!_tradeRegistry.IsFlat)
        {
            foreach (var group in _tradeRegistry.ActiveGroups.ToArray())
            {
                ManagePosition(bar, subscription, orders, group);
            }
        }

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

    // ── Phase 2 implementation ──

    private void ManagePosition(
        Int64Bar bar, DataSubscription sub, IOrderContext orders, OrderGroup group)
    {
        // 2a: Strategy-specific stop adjustment
        var newStop = OnAdjustStopLoss(bar, Context, group);

        // 2b: Evaluate exit rules
        var exitSignal = OnEvaluateExit(bar, Context, group);

        // Emit exit evaluation event
        EventBus.Emit(new ExitEvaluationEvent(
            bar.Timestamp, GetType().Name, sub.Asset.Name,
            group.GroupId, [], exitSignal, exitSignal <= Params.ExitThreshold));

        // 2c: Act on decision
        if (exitSignal <= Params.ExitThreshold)
        {
            var exitPrice = OnGetExitPrice(bar, group);
            _tradeRegistry.LiquidateGroup(group.GroupId, orders);
            OnGroupLiquidated(group.GroupId);
            EmitSignal(bar.Timestamp, "Exit", sub.Asset.Name,
                "Close", exitSignal, $"exit_score={exitSignal}");
        }
        else if (newStop is not null && newStop.Value != group.SlPrice)
        {
            _tradeRegistry.UpdateStopLoss(group.GroupId, newStop.Value, orders);
        }
    }

    // ── Phase 3 implementation ──

    private void EvaluateEntry(Int64Bar bar, DataSubscription sub, IOrderContext orders)
    {
        // 3a: Signal generation [STRATEGY-SPECIFIC]
        var signalStrength = OnGenerateSignal(bar, Context);
        if (Math.Abs(signalStrength) < Params.SignalThreshold)
            return;

        // Derive direction from sign: positive = Buy, negative = Sell
        var direction = signalStrength > 0 ? OrderSide.Buy : OrderSide.Sell;

        // 3b: Entry price [STRATEGY-SPECIFIC]
        var (entryPrice, orderType) = OnGetEntryPrice(bar, direction, Context);

        // 3c: Risk levels [STRATEGY-SPECIFIC]
        var (stopLoss, takeProfits) = OnGetRiskLevels(bar, direction, entryPrice, Context);

        // Validate SL is on correct side
        if (entryPrice != 0) // non-market orders have known entry price
        {
            if (direction == OrderSide.Buy && stopLoss >= entryPrice) return;
            if (direction == OrderSide.Sell && stopLoss <= entryPrice) return;
        }
        else // market order: use Close as proxy
        {
            if (direction == OrderSide.Buy && stopLoss >= bar.Close) return;
            if (direction == OrderSide.Sell && stopLoss <= bar.Close) return;
        }

        // 3d: Position sizing [infrastructure]
        var quantity = _moneyManagement.CalculateSize(
            entryPrice != 0 ? entryPrice : bar.Close, stopLoss, Context, sub.Asset);
        if (quantity < sub.Asset.MinOrderQuantity)
            return;

        // 3e: Order submission [STRATEGY-SPECIFIC with default]
        OnExecuteEntry(sub.Asset, direction, orderType, entryPrice,
            stopLoss, takeProfits, quantity, Context, orders);

        EmitSignal(bar.Timestamp, "Entry", sub.Asset.Name,
            direction.ToString(), signalStrength,
            $"type={orderType}, sl={stopLoss}, qty={quantity}");
    }

    // ── Abstract: the ONE method every strategy MUST implement ──

    /// <summary>
    /// Returns a signed signal score: positive = Buy, negative = Sell, 0 = no signal.
    /// Magnitude indicates conviction (e.g., +80 = Buy strength 80, -80 = Sell strength 80).
    /// Compared against <c>Params.SignalThreshold</c> via absolute value.
    /// </summary>
    protected abstract int OnGenerateSignal(Int64Bar bar, TContext context);

    // ── Virtual: override to customize, defaults handle common cases ──

    protected virtual (long price, OrderType type) OnGetEntryPrice(
        Int64Bar bar, OrderSide direction, TContext context)
        => (0, OrderType.Market);

    protected virtual (long stopLoss, TpLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, TContext context)
    {
        var atr = context is IVolatilityContext vol ? vol.Current : 0L;
        if (atr == 0) atr = bar.Close / 50; // fallback: 2% of price
        var mult = Params.DefaultAtrStopMultiplier;
        var distance = (long)(mult * atr);
        var sl = direction == OrderSide.Buy
            ? (entryPrice != 0 ? entryPrice : bar.Close) - distance
            : (entryPrice != 0 ? entryPrice : bar.Close) + distance;
        return (sl, []);
    }

    protected virtual void OnExecuteEntry(
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

    protected virtual int OnEvaluateExit(
        Int64Bar bar, TContext context, OrderGroup group) => 0;

    protected virtual long? OnAdjustStopLoss(
        Int64Bar bar, TContext context, OrderGroup group) => null;

    protected virtual long OnGetExitPrice(Int64Bar bar, OrderGroup group) => 0;

    // ── Optional hooks ──

    protected virtual void OnStrategyInit() { }
    protected virtual void OnContextUpdated(Int64Bar bar, DataSubscription sub) { }
    protected virtual void OnOrderFilled(Fill fill, Order order) { }
    protected virtual void OnGroupLiquidated(long groupId) { }
}
