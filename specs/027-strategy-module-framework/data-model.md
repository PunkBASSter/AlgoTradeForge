# Data Model: Strategy Module Framework

**Branch**: `027-strategy-module-framework` | **Date**: 2026-04-02

## Entity Map

### Core Framework Entities (New)

#### ModularStrategyBase<TParams>
- **Extends**: `StrategyBase<TParams>`
- **Implements**: `ITradeRegistryProvider`
- **Where**: `TParams : ModularStrategyParamsBase`
- **Fields**:
  - `_filters: List<IFilterModule>` — registered filter modules
  - `_tradeRegistry: TradeRegistryModule` — always present (from params)
  - `_moneyManagement: MoneyManagementModule` — always present (from params)
  - `_exit: ExitModule?` — optional exit rule aggregator
  - `_trailingStop: TrailingStopModule?` — optional trailing stop
  - `_regimeDetector: RegimeDetectorModule?` — optional regime classifier
  - `Context: StrategyContext` — per-bar shared state (protected)
- **Sealed methods**: `OnInit()`, `OnBarComplete()`, `OnTrade()`
- **Abstract methods**: `OnGenerateSignal(bar, context) → int` (signed: +N = Buy, -N = Sell)
- **Virtual methods**: `OnGetEntryPrice()`, `OnGetRiskLevels()`, `OnExecuteEntry()`, `OnEvaluateExit()`, `OnGetExitPrice()`, `OnStrategyInit()`, `OnContextUpdated()`, `OnOrderFilled()`
- **Registration methods**: `AddFilter()`, `SetExit()`, `SetTrailingStop()`, `SetRegimeDetector()`

#### ModularStrategyParamsBase
- **Extends**: `StrategyParamsBase`
- **Fields**:
  - `FilterThreshold: int` — [Optimizable, Min=-50, Max=50, Step=10], default 0
  - `SignalThreshold: int` — [Optimizable, Min=10, Max=80, Step=10], default 30
  - `ExitThreshold: int` — [Optimizable, Min=-100, Max=-20, Step=10], default -50
  - `DefaultAtrStopMultiplier: double` — [Optimizable, Min=1.0, Max=5.0, Step=0.5], default 2.0
  - `MoneyManagement: MoneyManagementParams` — always present
  - `TradeRegistry: TradeRegistryParams` — always present (existing type)
  - `TrailingStop: TrailingStopParams?` — optional
  - `Exit: ExitParams?` — optional
  - `RegimeDetector: RegimeDetectorParams?` — optional
  - `FilterWeights: Dictionary<string, int>` — weights per filter module key
- **Methods**: `GetFilterWeight(IFilterModule) → int`

#### StrategyContext
- **Fields**:
  - `CurrentBar: Int64Bar` — current bar data
  - `CurrentSubscription: DataSubscription` — current subscription
  - `Equity: long` — portfolio equity (ticks)
  - `Cash: long` — available cash (ticks)
  - `CurrentRegime: MarketRegime` — written by RegimeDetector, default Unknown
  - `CurrentAtr: long` — written by strategy, read by default risk levels
  - `CurrentVolatility: double` — written by strategy/module
  - `_data: Dictionary<string, object>` — loosely-coupled key-value store
- **Methods**: `Set<T>(key, value)`, `Get<T>(key) → T?`, `Has(key) → bool`, `Update(bar, sub, orders)`

### Module Interfaces (New)

#### IFilterModule
- **Extends**: `IStrategyModule`
- **Methods**:
  - `Initialize(IIndicatorFactory factory, DataSubscription subscription)`
  - `Evaluate(Int64Bar bar, OrderSide proposedSide) → int` — returns [-100, +100]

#### IExitRule
- **Fields**: `Name: string`
- **Methods**: `Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group) → int` — returns [-100, +100]

#### ExitModule
- **Implements**: `IStrategyModule`
- **Fields**: `_rules: List<IExitRule>`
- **Methods**:
  - `AddRule(IExitRule rule)`
  - `Evaluate(bar, context, group) → int` — returns most negative score

### Module Implementations (New)

#### TrailingStopModule
- **Implements**: `IStrategyModule<TrailingStopParams>`
- **Internal state**: `Dictionary<long, TrailingStopState>` keyed by group ID
- **Methods**:
  - `Activate(groupId, entryPrice, direction, initialStop)` — creates group entry
  - `Update(groupId, bar) → long?` — ratchets stop, returns new level or null
  - `GetCurrentStop(groupId) → long?`
  - `Remove(groupId)` — cleans up on group close

#### TrailingStopState (value type)
- **Fields**: `CurrentStop: long`, `Direction: OrderSide`, `ActivationPrice: long`, `HighWaterMark: long`

#### TrailingStopParams
- **Extends**: `ModuleParamsBase`
- **Fields**:
  - `Variant: TrailingStopVariant` — enum: Atr, Chandelier, Donchian
  - `AtrMultiplier: double` — [Optimizable, Min=1.0, Max=5.0, Step=0.5], default 2.0
  - `AtrPeriod: int` — [Optimizable, Min=5, Max=50, Step=5], default 14
  - `DonchianPeriod: int` — [Optimizable, Min=5, Max=50, Step=5], default 20

#### MoneyManagementModule
- **Implements**: `IStrategyModule<MoneyManagementParams>`
- **Methods**: `CalculateSize(entryPrice, stopLoss, context, asset) → decimal`

#### MoneyManagementParams
- **Extends**: `ModuleParamsBase`
- **Fields**:
  - `Method: SizingMethod` — enum: FixedFractional, AtrVolTarget, HalfKelly
  - `RiskPercent: double` — [Optimizable, Min=0.5, Max=5.0, Step=0.5], default 1.0
  - `VolTarget: double` — [Optimizable, Min=0.05, Max=0.3, Step=0.05], default 0.15
  - `WinRate: double` — for Kelly, [Optimizable, Min=0.3, Max=0.7, Step=0.05], default 0.5
  - `PayoffRatio: double` — for Kelly, [Optimizable, Min=1.0, Max=4.0, Step=0.5], default 2.0

#### RegimeDetectorModule
- **Implements**: `IStrategyModule<RegimeDetectorParams>`
- **Methods**:
  - `Initialize(IIndicatorFactory factory, DataSubscription subscription)`
  - `Update(Int64Bar bar, StrategyContext context)` — classifies regime, writes to context

#### RegimeDetectorParams
- **Extends**: `ModuleParamsBase`
- **Fields**:
  - `AdxPeriod: int` — [Optimizable, Min=7, Max=28, Step=7], default 14
  - `TrendThreshold: double` — [Optimizable, Min=15, Max=35, Step=5], default 25.0

#### CrossAssetModule
- **Implements**: `IStrategyModule<CrossAssetParams>`
- **Methods**:
  - `Initialize(IIndicatorFactory factory, DataSubscription sub1, DataSubscription sub2)`
  - `Update(Int64Bar bar, DataSubscription sub, StrategyContext context)` — writes z-score, hedge ratio, cointegration status to context

#### CrossAssetParams
- **Extends**: `ModuleParamsBase`
- **Fields**:
  - `LookbackPeriod: int` — [Optimizable, Min=20, Max=120, Step=10], default 60
  - `ZScoreEntryThreshold: double` — [Optimizable, Min=1.0, Max=3.0, Step=0.25], default 2.0
  - `ZScoreExitThreshold: double` — [Optimizable, Min=0.0, Max=1.5, Step=0.25], default 0.5

### Enums (New)

#### MarketRegime
- `Unknown`, `Trending`, `RangeBound`, `HighVolatility`

#### TrailingStopVariant
- `Atr`, `Chandelier`, `Donchian`

#### SizingMethod
- `FixedFractional`, `AtrVolTarget`, `HalfKelly`

### Built-in Exit Rules (New)

| Rule | Key Params | Score -100 When |
|------|-----------|-----------------|
| TimeBasedExitRule | MaxHoldBars: int | barsHeld >= MaxHoldBars |
| ProfitTargetExitRule | AtrMultiple: double | unrealizedPnL >= N × ATR |
| SignalReversalExitRule | (uses strategy's OnGenerateSignal) | signal flipped sign |
| RegimeChangeExitRule | (reads context.CurrentRegime) | regime ≠ entry regime |
| SessionCloseExitRule | CloseHourUtc: int | current UTC hour == CloseHourUtc |
| CointegrationBreakExitRule | PValueThreshold: double | ADF p-value > threshold |

### Built-in Filter Modules (Updated + New)

| Filter | Key Params | Score Logic |
|--------|-----------|-------------|
| AtrVolatilityFilterModule (refactored) | MinAtr, MaxAtr, Period | 100 if ATR in range, 0 if out |
| RegimeFilterModule (new) | AllowedRegimes | 100 if regime in allowed set, -100 if not |

### New Indicators

| Indicator | Base Class | Buffers | Use |
|-----------|-----------|---------|-----|
| Rsi | DoubleIndicatorBase (new) | "Value" (double, 0-100) | RSI(2) model strategy |
| Sma | Int64IndicatorBase | "Value" (long, price-scaled) | RSI(2) trend filter |
| DonchianChannel | Int64IndicatorBase | "Upper", "Lower", "Middle" (long) | Donchian model strategy |
| Adx | DoubleIndicatorBase (new) | "Value" (double, 0-100) | RegimeDetectorModule |

### New Event Types

#### FilterEvaluationEvent
- **Fields**: Timestamp, Source, AssetName, FilterScores (Dictionary<string, int>), CompositeScore (int), Passed (bool)
- **ExportMode**: Backtest | Live

#### ExitEvaluationEvent
- **Fields**: Timestamp, Source, AssetName, GroupId (long), RuleScores (Dictionary<string, int>), CompositeScore (int), ExitTriggered (bool)
- **ExportMode**: Backtest | Live

### Existing Entities (Unchanged)

- **OrderGroup** — used as-is for trade lifecycle
- **TradeRegistryModule** — used as-is (instantiated from params)
- **TradeRegistryParams** — used as-is (nested in ModularStrategyParamsBase)
- **IOrderContext** — consumed by pipeline for order submission

## Relationships

```
ModularStrategyBase<TParams>
├── owns StrategyContext (1:1, per bar)
├── owns TradeRegistryModule (1:1, always present)
├── owns MoneyManagementModule (1:1, always present)
├── owns TrailingStopModule? (0..1, optional)
├── owns ExitModule? (0..1, optional)
│   └── contains IExitRule[] (0..N)
├── owns RegimeDetectorModule? (0..1, optional)
├── owns IFilterModule[] (0..N, registered)
└── implements ITradeRegistryProvider (for live reconciliation)

TrailingStopModule
└── tracks TrailingStopState per OrderGroup (1:N by group ID)

StrategyContext
├── reads: bar, equity, cash (from base class)
├── reads/writes: regime (from RegimeDetectorModule)
├── reads/writes: ATR (from strategy's own indicator)
└── reads/writes: arbitrary data (via key-value store)
```
