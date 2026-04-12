# Strategy Framework — Module Pipeline Architecture

**Status: IMPLEMENTED** (2026-04-03) — All phases complete. Three model strategies (RSI2, Donchian Breakout, Pairs Trading) validated. 932+ tests passing.

**The universal bar-processing pipeline that every AlgoTradeForge strategy follows, decomposed into infrastructure (base class) and strategy-specific (override) responsibilities.**

Every strategy — mean-reversion, trend-following, volatility breakout, pairs trading, rotation — processes each bar through the same three-phase pipeline: **Update → Manage → Enter**. The base class (`ModularStrategyBase<TParams>`) orchestrates all infrastructure steps. A concrete strategy only implements three abstract/virtual methods that encode its unique trading logic.

---

## The Three-Phase Pipeline

```
OnBarComplete(bar, subscription, orders)
│
├── PHASE 1: UPDATE CONTEXT ──── pure computation, no orders
├── PHASE 2: MANAGE POSITIONS ── exits, trailing stops, SL updates
└── PHASE 3: EVALUATE ENTRY ──── filters → signal → price → risk → size → submit
```

### Phase 1: Update Context

Refreshes all derived state from the new bar. No trading decisions, no orders — only computation. Every module that produces derived data runs here.

```
1a. Indicators computed by engine          (existing — automatic via IIndicatorFactory)
1b. RegimeDetector.Update(bar)             → writes MarketRegime to StrategyContext
1c. VolatilityEstimator.Update(bar)        → writes current vol estimate to context
1d. CrossAssetModule.Update(bar, sub)      → writes z-score, hedge ratio, coint status
1e. Context snapshot                       → equity, position state, bars-since-entry
```

**Which strategies need what:**

| Step | Mean-Rev | Trend | Squeeze | Pairs | Rotation | Hybrid |
|------|----------|-------|---------|-------|----------|--------|
| 1a Indicators | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| 1b Regime | — | ✓ | ✓ | — | — | ✓ |
| 1c Volatility | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| 1d CrossAsset | — | — | — | ✓ | — | — |
| 1e Snapshot | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

Steps for unregistered modules are no-ops. Zero overhead when unused.

### Phase 2: Manage Existing Positions

Evaluates and acts on every active `OrderGroup` tracked by `TradeRegistry`. Runs only when positions are open — skipped entirely when flat.

```
For each active OrderGroup:
│
├─ 2a. TrailingStop.Update(bar)
│      Ratchet stop level (only moves in favorable direction).
│      If stop moved → queue SL update on the OrderGroup.
│
├─ 2b. ExitModule.Evaluate(bar, context, group) → exitSignal [-100, +100]
│      Aggregates multiple exit rules. Most extreme score wins:
│      ┌──────────────────────────────────────────────────────────┐
│      │ Rule               │ Condition                │ Score   │
│      ├──────────────────────────────────────────────────────────┤
│      │ Time-based         │ barsHeld > MaxHoldBars   │ -100    │
│      │ Session close      │ hour == CloseHourUtc     │ -100    │
│      │ Regime change      │ regime ≠ entryRegime     │ -80     │
│      │ Profit target      │ PnL > N × ATR            │ -60     │
│      │ Signal reversal    │ signal flipped sign       │ -70     │
│      │ Coint. breakdown   │ ADF p > 0.05 (pairs)     │ -100    │
│      │ Trailing stop hit  │ price breached stop       │ -100    │
│      └──────────────────────────────────────────────────────────┘
│
└─ 2c. Decision Gate
       │
       ├─ exitSignal ≤ ExitThreshold → CLOSE POSITION
       │   ├─ Determine exit price (market default, or virtual override)
       │   ├─ TradeRegistry.CloseGroup(groupId)
       │   ├─ Submit close order(s) via IOrderContext
       │   ├─ Emit SignalEvent(direction=Close, strength=exitSignal)
       │   └─ For pairs: close BOTH legs simultaneously
       │
       └─ exitSignal > ExitThreshold → UPDATE PROTECTION ONLY
           └─ If trailing stop ratcheted → update SL on OrderGroup
```

**Exit rule composition per strategy type:**

| Strategy Type | Primary Exit | Secondary Exit | Hard Kill |
|---------------|-------------|----------------|-----------|
| Mean-reversion | Signal reversal (RSI crosses 50) | Time-based (N bars) | — |
| Trend-following | Trailing stop | Regime change | — |
| Squeeze breakout | Trailing stop | Profit target (N×ATR) | — |
| Pairs trading | Z-score exit (±0.5) | — | Cointegration break |
| Rotation | Rebalance timer | — | — |
| Hybrid | Depends on active sub-strategy | Regime change | — |

### Phase 3: Evaluate New Entry

The entry pipeline runs only when capacity allows. Each step is a gate — failure at any point short-circuits the rest.

```
3a. CAPACITY CHECK ──────────────────────────────────── [infrastructure]
    TradeRegistry.CanOpenNew()
    → ActiveGroups.Count < MaxConcurrentGroups?
    → NO → STOP (at capacity, skip everything below)

3b. FILTER GATE ─────────────────────────────────────── [infrastructure]
    For each registered IFilterModule:
        scoreᵢ = Filterᵢ.Evaluate(bar, proposedSide)
    compositeFilter = Σ(weightᵢ × scoreᵢ) / Σ(weightᵢ)
    → compositeFilter < FilterThreshold? → STOP

3c. SIGNAL GENERATION ───────────────────────────────── [★ STRATEGY-SPECIFIC]
    ┌─────────────────────────────────────────────────────────────────────┐
    │  abstract int OnGenerateSignal(bar, context)                        │
    │                                                                     │
    │  Returns: signed score [-100, +100]; + = Buy, - = Sell              │
    │  Returns 0 or near-zero when no signal present                     │
    │                                                                     │
    │  This is THE method that differs per strategy.                      │
    │  Everything before it (filters) and after it (sizing, submission)   │
    │  is infrastructure.                                                 │
    └─────────────────────────────────────────────────────────────────────┘
    → |signalStrength| < SignalThreshold? → STOP

3d. ENTRY PRICE ─────────────────────────────────────── [★ STRATEGY-SPECIFIC]
    ┌─────────────────────────────────────────────────────────────────────┐
    │  virtual (long price, OrderType type)                               │
    │      OnGetEntryPrice(bar, direction, context)                       │
    │                                                                     │
    │  Default: (0, OrderType.Market) — fill at next available price      │
    │                                                                     │
    │  Override examples:                                                  │
    │  • Breakout: (DonchianUpper, OrderType.Stop)                        │
    │  • Mean-rev: (LowerBollingerBand, OrderType.Limit)                  │
    │  • ZigZag:   (lastPeakHigh, OrderType.Stop)                         │
    └─────────────────────────────────────────────────────────────────────┘

3e. RISK LEVELS ─────────────────────────────────────── [★ STRATEGY-SPECIFIC]
    ┌─────────────────────────────────────────────────────────────────────┐
    │  virtual (long stopLoss, TakeProfitLevel[] tps)                     │
    │      OnGetRiskLevels(bar, direction, entryPrice, context)           │
    │                                                                     │
    │  Default: SL = entry ∓ DefaultAtrMultiplier × ATR, no TPs           │
    │                                                                     │
    │  Override examples:                                                  │
    │  • ATR-based:      SL = entry - 2×ATR                               │
    │  • Structure:      SL = last ZigZag swing low                        │
    │  • Bollinger:      SL = opposite band                                │
    │  • Z-score (pairs): SL at z = ±3.0                                   │
    │  • Multi-TP:       TP₁=1R@50%, TP₂=2R@30%, TP₃=3R@20%              │
    └─────────────────────────────────────────────────────────────────────┘

3f. POSITION SIZING ─────────────────────────────────── [infrastructure]
    MoneyManagement.CalculateSize(entry, stopLoss, context)
    ├─ FixedFractional: qty = (equity × risk%) / |entry − SL|
    ├─ AtrVolTarget:    qty = (equity × volTarget) / (ATR × multiplier)
    └─ HalfKelly:       qty = f(winRate, payoffRatio) × equity / price
    Then:
    ├─ Asset.RoundQuantityDown(qty)
    ├─ Clamp to [MinOrderQuantity, MaxOrderQuantity]
    └─ qty < MinOrderQuantity? → STOP (can't afford the trade)

3g. ORDER SUBMISSION ────────────────────────────────── [infrastructure]
    ┌─────────────────────────────────────────────────────────────────────┐
    │  virtual void OnExecuteEntry(                                       │
    │      asset, direction, orderType, entryPrice,                       │
    │      stopLoss, takeProfits, quantity, context, orders)              │
    │                                                                     │
    │  Default: single order submission via TradeRegistry                  │
    │                                                                     │
    │  Override for:                                                       │
    │  • Pairs trading: submit BOTH legs as linked OrderGroup              │
    │  • Rotation: batch submit N orders (close stale + open new)          │
    │  • Pyramiding: add unit to existing OrderGroup                       │
    └─────────────────────────────────────────────────────────────────────┘
    Then:
    ├─ TradeRegistry.CreateOrderGroup(asset, direction, type, entry, sl, tps, qty)
    ├─ IOrderContext.Submit(order)
    ├─ Emit SignalEvent(direction, signalStrength, reason)
    └─ Emit RiskEvent(passed=true, method, qty, riskPercent)
```

---

## Responsibility Split: Infrastructure vs. Strategy-Specific

| Pipeline Step | Owner | What It Does |
|---|---|---|
| 1a–1e Context Update | `ModularStrategyBase` | Iterates registered modules, calls `Update()` |
| 2a Trailing Stop | `TrailingStopModule` | Ratchets stop, queues SL update |
| 2b Exit Evaluation | `ExitModule` | Aggregates exit rule scores |
| 2c Exit Execution | `ModularStrategyBase` | Closes via `TradeRegistry` |
| 3a Capacity Check | `TradeRegistry` | `CanOpenNew()` |
| 3b Filter Gate | `ModularStrategyBase` | Iterates `IFilterModule[]`, aggregates weighted scores |
| **3c Signal Generation** | **Concrete Strategy** | **`abstract OnGenerateSignal()`** |
| **3d Entry Price** | **Concrete Strategy** | **`virtual OnGetEntryPrice()` — default: market** |
| **3e Risk Levels** | **Concrete Strategy** | **`virtual OnGetRiskLevels()` — default: ATR-based** |
| 3f Position Sizing | `MoneyManagementModule` | Calculates qty from risk parameters |
| **3g Order Submission** | **Concrete Strategy** | **`virtual OnExecuteEntry()` — default: single order** |

A minimal strategy overrides only `OnGenerateSignal()`. A fully customized strategy overrides all four virtual methods.

---

## Base Class Contract

### ModularStrategyBase&lt;TParams&gt;

Extends the existing `StrategyBase<TParams>`. Adds module orchestration and the three-phase pipeline. Concrete strategies inherit from this instead of `StrategyBase` directly.

```csharp
public abstract class ModularStrategyBase<TParams> : StrategyBase<TParams>
    where TParams : ModularStrategyParamsBase
{
    // ── Module registry ──────────────────────────────────────────────

    private readonly List<IFilterModule> _filters = [];
    private TradeRegistryModule _tradeRegistry;
    private MoneyManagementModule _moneyManagement;
    private ExitModule? _exit;
    private TrailingStopModule? _trailingStop;
    private RegimeDetectorModule? _regimeDetector;

    protected StrategyContext Context { get; private set; }

    // ── Module registration (called in constructor of concrete strategy) ─

    protected void AddFilter(IFilterModule filter) => _filters.Add(filter);
    protected void SetExit(ExitModule exit) => _exit = exit;
    protected void SetTrailingStop(TrailingStopModule stop) => _trailingStop = stop;
    protected void SetRegimeDetector(RegimeDetectorModule detector) => _regimeDetector = detector;
    // TradeRegistry and MoneyManagement are always present (injected via params)

    // ── Lifecycle: sealed orchestration ──────────────────────────────

    public sealed override void OnInit()
    {
        Context = new StrategyContext();
        // Initialize all registered modules with IIndicatorFactory + subscriptions
        InitializeModules();
        OnStrategyInit();  // virtual hook for strategy-specific init
    }

    public sealed override void OnBarComplete(
        Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        // ── PHASE 1: UPDATE ──
        Context.Update(bar, subscription, orders);
        _regimeDetector?.Update(bar, Context);
        UpdateModules(bar, subscription);
        OnContextUpdated(bar, subscription);  // virtual hook (rarely needed)

        // ── PHASE 2: MANAGE ──
        if (!_tradeRegistry.IsFlat)
        {
            foreach (var group in _tradeRegistry.ActiveGroups)
            {
                ManagePosition(bar, subscription, orders, group);
            }
        }

        // ── PHASE 3: ENTER ──
        if (_tradeRegistry.CanOpenNew())
        {
            EvaluateEntry(bar, subscription, orders);
        }
    }

    public sealed override void OnTrade(Fill fill, Order order)
    {
        _tradeRegistry.OnFill(fill, order);
        OnOrderFilled(fill, order);  // virtual hook for strategy-specific fill handling
    }

    // ── Phase 2 implementation ───────────────────────────────────────

    private void ManagePosition(
        Int64Bar bar, DataSubscription sub, IOrderContext orders, OrderGroup group)
    {
        // 2a: Ratchet trailing stop
        long? newStop = null;
        if (_trailingStop is not null)
        {
            _trailingStop.Update(bar, group.Direction);
            newStop = _trailingStop.CurrentStop;
        }

        // 2b: Evaluate exit rules
        var exitSignal = _exit?.Evaluate(bar, Context, group) ?? 0;

        // Allow strategy to inject custom exit logic
        var customExit = OnEvaluateExit(bar, Context, group);
        exitSignal = Math.Min(exitSignal, customExit);  // most negative wins

        // 2c: Act on decision
        if (exitSignal <= Params.ExitThreshold)
        {
            var exitPrice = OnGetExitPrice(bar, group);
            _tradeRegistry.CloseGroup(group.Id, orders, exitPrice);
            EmitSignal(bar.Timestamp, "Exit", sub.Asset.Name,
                "Close", exitSignal, $"exit_score={exitSignal}");
        }
        else if (newStop is not null && newStop != group.CurrentStopLoss)
        {
            _tradeRegistry.UpdateStopLoss(group.Id, newStop.Value, orders);
        }
    }

    // ── Phase 3 implementation ───────────────────────────────────────

    private void EvaluateEntry(
        Int64Bar bar, DataSubscription sub, IOrderContext orders)
    {
        // 3b: Filter gate
        var filterScore = EvaluateFilters(bar);
        if (filterScore < Params.FilterThreshold)
            return;

        // 3c: Signal generation [★ STRATEGY-SPECIFIC]
        var signalStrength = OnGenerateSignal(bar, Context);
        if (Math.Abs(signalStrength) < Params.SignalThreshold)
            return;
        var direction = signalStrength > 0 ? OrderSide.Buy : OrderSide.Sell;

        // Reconcile signal direction with filter allowance
        if (direction == OrderSide.Buy && filterScore < 0) return;
        if (direction == OrderSide.Sell && filterScore > 0) return;

        // 3d: Entry price [★ STRATEGY-SPECIFIC]
        var (entryPrice, orderType) = OnGetEntryPrice(bar, direction, Context);

        // 3e: Risk levels [★ STRATEGY-SPECIFIC]
        var (stopLoss, takeProfits) = OnGetRiskLevels(bar, direction, entryPrice, Context);

        // Validate SL is on correct side
        if (direction == OrderSide.Buy && stopLoss >= entryPrice) return;
        if (direction == OrderSide.Sell && stopLoss <= entryPrice) return;

        // 3f: Position sizing [infrastructure]
        var quantity = _moneyManagement.CalculateSize(
            entryPrice, stopLoss, Context, sub.Asset);
        if (quantity < sub.Asset.MinOrderQuantity)
            return;

        // 3g: Order submission [★ STRATEGY-SPECIFIC with default]
        OnExecuteEntry(sub.Asset, direction, orderType, entryPrice,
            stopLoss, takeProfits, quantity, Context, orders);

        EmitSignal(bar.Timestamp, "Entry", sub.Asset.Name,
            direction.ToString(), signalStrength,
            $"type={orderType}, sl={stopLoss}, qty={quantity}");
    }

    private int EvaluateFilters(Int64Bar bar)
    {
        if (_filters.Count == 0) return 100;  // no filters = always allowed

        var weightedSum = 0;
        var totalWeight = 0;
        foreach (var filter in _filters)
        {
            var weight = Params.GetFilterWeight(filter);
            weightedSum += weight * filter.Evaluate(bar, OrderSide.Buy);
            totalWeight += weight;
        }
        return totalWeight > 0 ? weightedSum / totalWeight : 100;
    }

    // ── Abstract: the ONE method every strategy MUST implement ────────

    /// <summary>
    /// Core signal logic. Returns signal strength [-100, +100].
    /// Positive = bullish, negative = bearish, 0 = no signal.
    /// Sets <paramref name="direction"/> to the proposed trade side.
    /// </summary>
    /// Signed score: positive = Buy, negative = Sell, 0 = no signal.
    protected abstract int OnGenerateSignal(Int64Bar bar, StrategyContext context);

    // ── Virtual: override to customize, defaults handle common cases ─

    /// <summary>
    /// Determines entry price and order type.
    /// Default: market order (price=0, type=Market).
    /// Override for limit/stop entries at specific levels.
    /// </summary>
    protected virtual (long price, OrderType type) OnGetEntryPrice(
        Int64Bar bar, OrderSide direction, StrategyContext context)
        => (0, OrderType.Market);

    /// <summary>
    /// Determines stop-loss and take-profit levels.
    /// Default: SL at entry ∓ DefaultAtrMultiplier × ATR, no TPs.
    /// Override for structure-based stops, multi-TP setups, etc.
    /// </summary>
    protected virtual (long stopLoss, TakeProfitLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, StrategyContext context)
    {
        var atr = context.CurrentAtr;
        var mult = Params.DefaultAtrStopMultiplier;
        var sl = direction == OrderSide.Buy
            ? entryPrice - (long)(mult * atr)
            : entryPrice + (long)(mult * atr);
        return (sl, []);
    }

    /// <summary>
    /// Submits the entry order(s). Default: single order via TradeRegistry.
    /// Override for pairs trading (two legs), rotation (batch), or pyramiding.
    /// </summary>
    protected virtual void OnExecuteEntry(
        Asset asset, OrderSide direction, OrderType orderType, long entryPrice,
        long stopLoss, TakeProfitLevel[] takeProfits, decimal quantity,
        StrategyContext context, IOrderContext orders)
    {
        _tradeRegistry.SubmitOrderGroup(
            asset, direction, orderType, entryPrice,
            stopLoss, takeProfits, quantity, orders);
    }

    /// <summary>
    /// Custom exit logic evaluated per bar for each active group.
    /// Default: 0 (no opinion). Return negative values to force exit.
    /// Composed with ExitModule scores — most negative wins.
    /// </summary>
    protected virtual int OnEvaluateExit(
        Int64Bar bar, StrategyContext context, OrderGroup group) => 0;

    /// <summary>
    /// Determines exit price. Default: 0 (market order).
    /// Override for limit exits at specific levels (e.g., mean-reversion target).
    /// </summary>
    protected virtual long OnGetExitPrice(Int64Bar bar, OrderGroup group) => 0;

    // ── Optional hooks ───────────────────────────────────────────────

    /// <summary>Called once during OnInit, after all modules are initialized.</summary>
    protected virtual void OnStrategyInit() { }

    /// <summary>Called after Phase 1 context update, before position management.</summary>
    protected virtual void OnContextUpdated(Int64Bar bar, DataSubscription sub) { }

    /// <summary>Called when a fill arrives, after TradeRegistry processes it.</summary>
    protected virtual void OnOrderFilled(Fill fill, Order order) { }
}
```

### ModularStrategyParamsBase

Extends `StrategyParamsBase` with pipeline configuration common to all modular strategies.

```csharp
public class ModularStrategyParamsBase : StrategyParamsBase
{
    // ── Pipeline thresholds ──────────────────────────────────────────

    /// <summary>Minimum composite filter score to allow entry. Default: 0.</summary>
    [Optimizable(Min = -50, Max = 50, Step = 10)]
    public int FilterThreshold { get; init; } = 0;

    /// <summary>Minimum |signal strength| to trigger entry. Default: 30.</summary>
    [Optimizable(Min = 10, Max = 80, Step = 10)]
    public int SignalThreshold { get; init; } = 30;

    /// <summary>Exit signal at or below this score triggers position close. Default: -50.</summary>
    [Optimizable(Min = -100, Max = -20, Step = 10)]
    public int ExitThreshold { get; init; } = -50;

    // ── Default risk parameters (used by base class default implementations) ─

    /// <summary>ATR multiplier for default stop-loss. Default: 2.0.</summary>
    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double DefaultAtrStopMultiplier { get; init; } = 2.0;

    // ── Module params (nested, each with own [Optimizable] attributes) ─

    public MoneyManagementParams MoneyManagement { get; init; } = new();
    public TradeRegistryParams TradeRegistry { get; init; } = new();
    public TrailingStopParams? TrailingStop { get; init; }
    public ExitParams? Exit { get; init; }
    public RegimeDetectorParams? RegimeDetector { get; init; }

    // ── Filter weights ──────────────────────────────────────────────

    /// <summary>
    /// Weights per filter module key. Keys match [ModuleKey] attribute values.
    /// Default weight = 1 if not specified.
    /// </summary>
    public Dictionary<string, int> FilterWeights { get; init; } = [];

    public int GetFilterWeight(IFilterModule filter)
    {
        var key = filter.GetType()
            .GetCustomAttribute<ModuleKeyAttribute>()?.Key;
        return key is not null && FilterWeights.TryGetValue(key, out var w) ? w : 1;
    }
}
```

---

## Module Interfaces

### IFilterModule

```csharp
/// <summary>
/// Evaluates whether a trade in the proposed direction is allowed.
/// Returns [-100, +100]: positive favors longs, negative favors shorts, 0 = neutral.
/// </summary>
public interface IFilterModule : IStrategyModule
{
    void Initialize(IIndicatorFactory factory, DataSubscription subscription);
    int Evaluate(Int64Bar bar, OrderSide proposedSide);
}
```

### IExitRule

Individual exit rules are composed inside the `ExitModule`. Each rule evaluates independently.

```csharp
/// <summary>
/// A single exit condition. Returns a score [-100, +100].
/// -100 = must close immediately. 0 = no opinion. +100 = strongly hold.
/// </summary>
public interface IExitRule
{
    string Name { get; }
    int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group);
}
```

### ExitModule

Aggregates multiple `IExitRule` instances. The most extreme (most negative) score wins.

```csharp
public sealed class ExitModule : IStrategyModule
{
    private readonly List<IExitRule> _rules = [];

    public void AddRule(IExitRule rule) => _rules.Add(rule);

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        if (_rules.Count == 0) return 0;
        var worstScore = 0;
        foreach (var rule in _rules)
        {
            var score = rule.Evaluate(bar, context, group);
            if (score < worstScore) worstScore = score;
        }
        return worstScore;
    }
}
```

Built-in exit rules:

| Rule Class | Logic | When Score = -100 |
|---|---|---|
| `TimeBasedExitRule` | Counts bars since entry | `barsHeld >= MaxHoldBars` |
| `SessionCloseExitRule` | Checks UTC hour | `currentHour == CloseHourUtc` |
| `RegimeChangeExitRule` | Compares current vs entry regime | Regime flipped |
| `ProfitTargetExitRule` | Checks unrealized PnL vs ATR multiple | `pnl >= N × ATR` |
| `SignalReversalExitRule` | Re-evaluates entry signal | Signal sign flipped |
| `CointegrationBreakExitRule` | Checks ADF p-value (pairs only) | `p > 0.05` |

### TrailingStopModule

```csharp
public sealed class TrailingStopModule : IStrategyModule
{
    public long CurrentStop { get; private set; }

    public void Activate(long entryPrice, OrderSide direction, long initialStop);
    public void Update(Int64Bar bar, OrderSide direction);
    // Internally: ratchets stop using selected variant (ATR/Chandelier/Donchian)
    // CurrentStop only moves in the favorable direction
    public void Reset();
}
```

### TradeRegistryModule

```csharp
public sealed class TradeRegistryModule : IStrategyModule
{
    public IReadOnlyList<OrderGroup> ActiveGroups { get; }
    public bool IsFlat => ActiveGroups.Count == 0;
    public bool CanOpenNew() => ActiveGroups.Count < _params.MaxConcurrentGroups;

    public OrderGroup SubmitOrderGroup(
        Asset asset, OrderSide direction, OrderType type, long entryPrice,
        long stopLoss, TakeProfitLevel[] takeProfits, decimal quantity,
        IOrderContext orders);

    public void CloseGroup(long groupId, IOrderContext orders, long exitPrice = 0);
    public void UpdateStopLoss(long groupId, long newStop, IOrderContext orders);
    public void OnFill(Fill fill, Order order);
    // Maps fill to owning OrderGroup, updates group state
    // Emits OrderGroupEvent to IEventBus
}
```

### MoneyManagementModule

```csharp
public sealed class MoneyManagementModule : IStrategyModule
{
    public decimal CalculateSize(
        long entryPrice, long stopLoss, StrategyContext context, Asset asset);
    // Dispatches to selected method (FixedFractional / AtrVolTarget / HalfKelly)
    // Applies regime-based reduction if configured
    // Respects asset quantity constraints
    // Emits RiskEvent to IEventBus
}
```

### StrategyContext

```csharp
public sealed class StrategyContext
{
    // ── Bar state ────────────────────────────────────────────────────
    public Int64Bar CurrentBar { get; private set; }
    public DataSubscription CurrentSubscription { get; private set; }

    // ── Position state ───────────────────────────────────────────────
    public long Equity { get; private set; }
    public long Cash { get; private set; }

    // ── Regime (written by RegimeDetector, read by filters/exits) ────
    public MarketRegime CurrentRegime { get; internal set; } = MarketRegime.Unknown;

    // ── Volatility (written by VolatilityEstimator or ATR indicator) ─
    public long CurrentAtr { get; internal set; }
    public long CurrentVolatility { get; internal set; }

    // ── Module-to-module data (loosely coupled) ─────────────────────
    private readonly Dictionary<string, object> _data = [];
    public void Set<T>(string key, T value) => _data[key] = value!;
    public T? Get<T>(string key) => _data.TryGetValue(key, out var v) ? (T)v : default;
    public bool Has(string key) => _data.ContainsKey(key);

    // ── Lifecycle ────────────────────────────────────────────────────
    internal void Update(Int64Bar bar, DataSubscription sub, IOrderContext orders)
    {
        CurrentBar = bar;
        CurrentSubscription = sub;
        Cash = orders.Cash;
        // Equity updated from portfolio state
    }
}
```

---

## Strategy Implementation Examples

### Minimal: RSI(2) Mean-Reversion

Overrides only `OnGenerateSignal()`. Uses all defaults for entry price (market), risk levels (ATR-based), and submission (single order).

```csharp
[StrategyKey("RSI2-MeanReversion")]
public sealed class Rsi2MeanReversionStrategy(
    Rsi2Params parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<Rsi2Params>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private Rsi _rsi = null!;
    private Sma _trendFilter = null!;

    protected override void OnStrategyInit()
    {
        _rsi = new Rsi(Params.RsiPeriod);
        Indicators.Create(_rsi, DataSubscriptions[0]);

        _trendFilter = new Sma(Params.TrendFilterPeriod);
        Indicators.Create(_trendFilter, DataSubscriptions[0]);

        AddFilter(new AtrVolatilityFilterModule(Params.AtrFilter));
    }

    protected override int OnGenerateSignal(Int64Bar bar, StrategyContext context)
    {
        var rsiValues = _rsi.Buffers["Value"];
        var smaValues = _trendFilter.Buffers["Value"];
        if (rsiValues.Count < 2 || smaValues.Count == 0) return 0;

        var rsi = rsiValues[^1];
        var sma = smaValues[^1];

        if (rsi < Params.OversoldThreshold && bar.Close > sma)
            return 80;   // Buy
        if (rsi > Params.OverboughtThreshold && bar.Close < sma)
            return -80;  // Sell

        return 0;
    }
}
```

### Moderate: Donchian Breakout (Modern Turtle)

Overrides `OnGenerateSignal()` + `OnGetEntryPrice()` + `OnGetRiskLevels()`. Uses stop orders for entry and structure-based stops.

```csharp
[StrategyKey("DonchianBreakout")]
public sealed class DonchianBreakoutStrategy(
    DonchianParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<DonchianParams>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private DonchianChannel _entryChannel = null!;
    private DonchianChannel _exitChannel = null!;
    private Atr _atr = null!;

    protected override void OnStrategyInit()
    {
        _entryChannel = new DonchianChannel(Params.EntryPeriod);
        _exitChannel = new DonchianChannel(Params.ExitPeriod);
        _atr = new Atr(Params.AtrPeriod);

        Indicators.Create(_entryChannel, DataSubscriptions[0]);
        Indicators.Create(_exitChannel, DataSubscriptions[0]);
        Indicators.Create(_atr, DataSubscriptions[0]);

        SetTrailingStop(new TrailingStopModule(Params.TrailingStop));
        SetRegimeDetector(new RegimeDetectorModule(Params.RegimeDetector));
        AddFilter(new RegimeFilter()); // only trade in trending regimes
    }

    protected override int OnGenerateSignal(Int64Bar bar, StrategyContext context)
    {
        var upper = _entryChannel.Buffers["Upper"];
        var lower = _entryChannel.Buffers["Lower"];
        if (upper.Count < 2) return 0;

        if (bar.Close > upper[^2])
            return 80;   // Buy — breakout above previous bar's channel
        if (bar.Close < lower[^2])
            return -80;  // Sell — breakout below
        return 0;
    }

    protected override (long price, OrderType type) OnGetEntryPrice(
        Int64Bar bar, OrderSide direction, StrategyContext context)
    {
        // Stop order at current channel boundary
        return direction == OrderSide.Buy
            ? (_entryChannel.Buffers["Upper"][^1], OrderType.Stop)
            : (_entryChannel.Buffers["Lower"][^1], OrderType.Stop);
    }

    protected override (long stopLoss, TakeProfitLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, StrategyContext context)
    {
        var atr = _atr.Buffers["Value"][^1];
        var sl = direction == OrderSide.Buy
            ? entryPrice - Params.AtrStopMultiplier * atr
            : entryPrice + Params.AtrStopMultiplier * atr;
        return ((long)sl, []);  // no fixed TPs — trailing stop handles exit
    }
}
```

### Advanced: Pairs Trading

Overrides `OnGenerateSignal()` + `OnGetRiskLevels()` + `OnExecuteEntry()` + `OnEvaluateExit()`. Uses two subscriptions, custom submission for both legs, and cointegration-break exit.

```csharp
[StrategyKey("PairsTrading")]
public sealed class PairsTradingStrategy(
    PairsTradingParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<PairsTradingParams>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private CrossAssetModule _crossAsset = null!;

    protected override void OnStrategyInit()
    {
        _crossAsset = new CrossAssetModule(Params.CrossAsset);
        _crossAsset.Initialize(Indicators, DataSubscriptions[0], DataSubscriptions[1]);
    }

    protected override int OnGenerateSignal(Int64Bar bar, StrategyContext context)
    {
        var z = context.Get<double>("crossasset.zscore");
        var cointValid = context.Get<bool>("crossasset.cointegrated");

        if (!cointValid) return 0;  // no trading when cointegration broken

        var strength = (int)Math.Min(Math.Abs(z) * 40, 100);
        if (z < -Params.CrossAsset.ZScoreEntryThreshold)
            return strength;   // Buy — long spread (buy A, sell B)
        if (z > Params.CrossAsset.ZScoreEntryThreshold)
            return -strength;  // Sell — short spread (sell A, buy B)
        return 0;
    }

    protected override (long stopLoss, TakeProfitLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, StrategyContext context)
    {
        // SL at extreme z-score (cointegration emergency)
        var atr = context.CurrentAtr;
        var sl = direction == OrderSide.Buy
            ? entryPrice - 3 * atr
            : entryPrice + 3 * atr;
        return ((long)sl, []);
    }

    protected override void OnExecuteEntry(
        Asset asset, OrderSide direction, OrderType orderType, long entryPrice,
        long stopLoss, TakeProfitLevel[] takeProfits, decimal quantity,
        StrategyContext context, IOrderContext orders)
    {
        // Submit BOTH legs as a linked pair
        var hedgeRatio = context.Get<decimal>("crossasset.hedge_ratio");
        var assetA = DataSubscriptions[0].Asset;
        var assetB = DataSubscriptions[1].Asset;

        var qtyA = quantity;
        var qtyB = assetB.RoundQuantityDown(quantity * hedgeRatio);

        // Long spread = buy A + sell B; Short spread = sell A + buy B
        var sideA = direction;
        var sideB = direction == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        orders.Submit(new Order { Id = 0, Asset = assetA, Side = sideA,
            Type = OrderType.Market, Quantity = qtyA });
        orders.Submit(new Order { Id = 0, Asset = assetB, Side = sideB,
            Type = OrderType.Market, Quantity = qtyB });
    }

    protected override int OnEvaluateExit(
        Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        var z = context.Get<double>("crossasset.zscore");
        var cointValid = context.Get<bool>("crossasset.cointegrated");

        // Hard kill on cointegration breakdown
        if (!cointValid) return -100;

        // Normal exit when z-score reverts past exit threshold
        if (group.Direction == OrderSide.Buy && z > -Params.CrossAsset.ZScoreExitThreshold)
            return -80;
        if (group.Direction == OrderSide.Sell && z < Params.CrossAsset.ZScoreExitThreshold)
            return -80;

        return 0;  // hold
    }
}
```

---

## How Each Strategy Type Maps to the Pipeline

| Strategy | OnGenerateSignal | OnGetEntryPrice | OnGetRiskLevels | OnExecuteEntry | OnEvaluateExit |
|---|---|---|---|---|---|
| RSI(2) Mean-Rev | RSI thresholds | default (market) | default (ATR) | default (single) | Signal reversal |
| Bollinger Rev | BB band touch | Limit at band | SL = opposite band | default | Mean target |
| Donchian Breakout | Channel breakout | Stop at channel | ATR-based | default | — (trailing stop) |
| EMA Crossover | EMA cross + ADX | default (market) | default (ATR) | default | Regime change |
| BB-KC Squeeze | Squeeze fire + MACD | default (market) | ATR-based | default | Profit target |
| Pairs Trading | Z-score threshold | default (market) | Z-score emergency | ★ Two legs | Coint. break |
| Momentum Rotation | Rank by return | default (market) | default (ATR) | ★ Batch rebalance | Rebalance timer |
| Regime Switcher | Delegates to sub | Delegates to sub | Delegates to sub | Delegates to sub | Regime change |
| Multi-Factor | Composite score | default (market) | default (ATR) | default | Score drop |

The pipeline handles every row. Strategies only fill in their unique columns — the `★` marks the rare cases where `OnExecuteEntry` needs customization.

---

## Compatibility with Existing Architecture

**BacktestEngine** — `ModularStrategyBase` implements `IInt64BarStrategy` via its sealed `OnBarComplete()`. The engine sees it as any other strategy. No engine changes required.

**Optimization** — `ModularStrategyParamsBase` extends `StrategyParamsBase`. Nested module params (`MoneyManagementParams`, `TrailingStopParams`, etc.) inherit `ModuleParamsBase` and carry `[Optimizable]` attributes. `OptimizationAxisResolver` discovers them via the same reflection path used for `AtrVolatilityFilterParams` today.

**Event Bus** — Every decision point emits events through the existing `IEventBus`: signals, risk checks, stop updates, group state changes. All implement `IBacktestEvent` with appropriate `ExportMode`.

**Debug Probe** — Module state (regime, trailing stop level, filter scores, signal strength) is observable via `IDebugProbe.OnBarProcessed()` through the existing `DebugSnapshot` mechanism.

**Thread Safety** — Each `BacktestEngine.Run()` creates a fresh strategy instance with fresh module instances. No shared mutable state. `Parallel.ForEach` over optimization trials works unchanged.

**Multi-Subscription** — The existing `DataSubscription[]` delivery loop in `BacktestEngine.DeliverBarsAtTimestamp()` feeds bars to `OnBarComplete()` per subscription. `ModularStrategyBase` routes each bar to the correct modules based on which subscription it belongs to.