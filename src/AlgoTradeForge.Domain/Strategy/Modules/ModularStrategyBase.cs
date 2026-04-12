using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public abstract class ModularStrategyBase<TParams>(TParams parameters, IIndicatorFactory? indicators = null)
    : StrategyBase<TParams>(parameters, indicators), ITradeRegistryProvider
    where TParams : ModularStrategyParamsBase
{
    private readonly List<IFilterModule> _filters = [];
    private readonly List<IIndicator<Int64Bar, long>> _longIndicators = [];
    private readonly List<IIndicator<Int64Bar, double>> _doubleIndicators = [];
    private readonly Dictionary<int, List<Int64Bar>> _barHistories = [];
    private TradeRegistryModule _tradeRegistry = null!;
    private MoneyManagementModule _moneyManagement = null!;
    private ExitModule? _exit;
    private TrailingStopModule? _trailingStop;
    private RegimeDetectorModule? _regimeDetector;

    protected StrategyContext Context { get; private set; } = null!;

    protected void RegisterIndicator(IIndicator<Int64Bar, long> indicator) =>
        _longIndicators.Add(indicator);

    protected void RegisterIndicator(IIndicator<Int64Bar, double> indicator) =>
        _doubleIndicators.Add(indicator);

    TradeRegistryModule ITradeRegistryProvider.TradeRegistry => _tradeRegistry;

    // ── Module registration (called in OnStrategyInit of concrete strategy) ──

    protected void AddFilter(IFilterModule filter) => _filters.Add(filter);
    protected void SetExit(ExitModule exit) => _exit = exit;
    protected void SetTrailingStop(TrailingStopModule stop) => _trailingStop = stop;
    protected void SetRegimeDetector(RegimeDetectorModule detector) => _regimeDetector = detector;

    // ── Lifecycle: sealed orchestration ──

    public sealed override void OnInit()
    {
        Context = new StrategyContext();
        _tradeRegistry = new TradeRegistryModule(Params.TradeRegistry);
        _moneyManagement = new MoneyManagementModule(Params.MoneyManagement);

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

        // Update filter modules with bar history
        foreach (var filter in _filters)
            filter.Update(history);

        Context.Update(bar, subscription, orders);
        _regimeDetector?.Update(bar, Context);
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
        // 2a: Ratchet trailing stop
        long? newStop = null;
        if (_trailingStop is not null)
        {
            newStop = _trailingStop.Update(group.GroupId, bar, Context.CurrentAtr);
        }

        // 2b: Evaluate exit rules
        var exitSignal = _exit?.Evaluate(bar, Context, group) ?? 0;

        // Allow strategy to inject custom exit logic
        var customExit = OnEvaluateExit(bar, Context, group);
        exitSignal = Math.Min(exitSignal, customExit); // most negative wins

        // Emit exit evaluation event
        EventBus.Emit(new ExitEvaluationEvent(
            bar.Timestamp, GetType().Name, sub.Asset.Name,
            group.GroupId, [], exitSignal, exitSignal <= Params.ExitThreshold));

        // 2c: Act on decision
        if (exitSignal <= Params.ExitThreshold)
        {
            var exitPrice = OnGetExitPrice(bar, group);
            _tradeRegistry.LiquidateGroup(group.GroupId, orders);
            _trailingStop?.Remove(group.GroupId);
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
        // 3b: Filter gate
        var (filterScore, filterScores) = EvaluateFilters(bar);

        EventBus.Emit(new FilterEvaluationEvent(
            bar.Timestamp, GetType().Name, sub.Asset.Name,
            filterScores, filterScore, filterScore >= Params.FilterThreshold));

        if (filterScore < Params.FilterThreshold)
            return;

        // 3c: Signal generation [STRATEGY-SPECIFIC]
        var signalStrength = OnGenerateSignal(bar, Context);
        if (Math.Abs(signalStrength) < Params.SignalThreshold)
            return;

        // Derive direction from sign: positive = Buy, negative = Sell
        var direction = signalStrength > 0 ? OrderSide.Buy : OrderSide.Sell;

        // Reconcile signal direction with filter
        if (direction == OrderSide.Buy && filterScore < 0) return;
        if (direction == OrderSide.Sell && filterScore > 0) return;

        // 3d: Entry price [STRATEGY-SPECIFIC]
        var (entryPrice, orderType) = OnGetEntryPrice(bar, direction, Context);

        // 3e: Risk levels [STRATEGY-SPECIFIC]
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

        // 3f: Position sizing [infrastructure]
        var quantity = _moneyManagement.CalculateSize(
            entryPrice != 0 ? entryPrice : bar.Close, stopLoss, Context, sub.Asset);
        if (quantity < sub.Asset.MinOrderQuantity)
            return;

        // 3g: Order submission [STRATEGY-SPECIFIC with default]
        OnExecuteEntry(sub.Asset, direction, orderType, entryPrice,
            stopLoss, takeProfits, quantity, Context, orders);

        EmitSignal(bar.Timestamp, "Entry", sub.Asset.Name,
            direction.ToString(), signalStrength,
            $"type={orderType}, sl={stopLoss}, qty={quantity}");
    }

    private (int score, Dictionary<string, int> perFilter) EvaluateFilters(Int64Bar bar)
    {
        var scores = new Dictionary<string, int>();
        if (_filters.Count == 0) return (100, scores);

        var weightedSum = 0;
        var totalWeight = 0;
        foreach (var filter in _filters)
        {
            var weight = Params.GetFilterWeight(filter);
            var score = filter.Evaluate(bar, OrderSide.Buy);
            var key = filter.GetType().Name;
            scores[key] = score;
            weightedSum += weight * score;
            totalWeight += weight;
        }

        var composite = totalWeight > 0 ? weightedSum / totalWeight : 100;
        return (composite, scores);
    }

    // ── Abstract: the ONE method every strategy MUST implement ──

    /// <summary>
    /// Returns a signed signal score: positive = Buy, negative = Sell, 0 = no signal.
    /// Magnitude indicates conviction (e.g., +80 = Buy strength 80, -80 = Sell strength 80).
    /// Compared against <c>Params.SignalThreshold</c> via absolute value.
    /// </summary>
    protected abstract int OnGenerateSignal(Int64Bar bar, StrategyContext context);

    // ── Virtual: override to customize, defaults handle common cases ──

    protected virtual (long price, OrderType type) OnGetEntryPrice(
        Int64Bar bar, OrderSide direction, StrategyContext context)
        => (0, OrderType.Market);

    protected virtual (long stopLoss, TpLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, StrategyContext context)
    {
        var atr = context.CurrentAtr;
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
        StrategyContext context, IOrderContext orders)
    {
        _tradeRegistry.OpenGroup(
            orders, asset, direction, orderType, quantity, stopLoss,
            takeProfits,
            entryLimitPrice: orderType == OrderType.Limit ? entryPrice : null,
            entryStopPrice: orderType == OrderType.Stop ? entryPrice : null);
    }

    protected virtual int OnEvaluateExit(
        Int64Bar bar, StrategyContext context, OrderGroup group) => 0;

    protected virtual long OnGetExitPrice(Int64Bar bar, OrderGroup group) => 0;

    // ── Optional hooks ──

    protected virtual void OnStrategyInit() { }
    protected virtual void OnContextUpdated(Int64Bar bar, DataSubscription sub) { }
    protected virtual void OnOrderFilled(Fill fill, Order order) { }
}
