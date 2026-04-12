# Research: Strategy Module Framework

**Branch**: `027-strategy-module-framework` | **Date**: 2026-04-02

## R1: Existing Strategy Base Class & Lifecycle

**Decision**: Extend `StrategyBase<TParams>` with a new `ModularStrategyBase<TParams>` that seals `OnBarComplete`, `OnInit`, and `OnTrade`.

**Rationale**: `StrategyBase<TParams>` already implements `IInt64BarStrategy`, `IEventBusReceiver`, and `IFeedContextReceiver`. It provides `EmitSignal()`, `EventBus`, `Feeds`, `Indicators`, and `Params`. Inheriting from it gives the modular base all existing integration points for free. The sealed methods prevent strategies from bypassing the pipeline.

**Alternatives considered**:
- Implement `IInt64BarStrategy` directly: rejected because it would duplicate all the event bus, feed context, and indicator factory wiring already in `StrategyBase`.
- Use composition (wrap a strategy): rejected because the backtest engine and live connector expect `IInt64BarStrategy` instances, not wrappers.

**Key file**: `src/AlgoTradeForge.Domain/Strategy/StrategyBase.cs`

## R2: Indicator Factory Integration

**Decision**: `ModularStrategyBase` does NOT manage indicator computation. Modules and strategies create indicators via `IIndicatorFactory` and call `Compute()` themselves in `OnStrategyInit()` and during bar processing.

**Rationale**: The existing system gives full ownership to strategies/modules. `BacktestEngine` calls `OnBarComplete()` with the full series available. Indicators compute incrementally by re-processing the series (already efficient via internal caching). Trying to centralize indicator management would break the existing convention and add complexity.

**Key files**: `src/AlgoTradeForge.Domain/Indicators/IIndicatorFactory.cs`, `src/AlgoTradeForge.Application/Indicators/EmittingIndicatorFactory.cs`

## R3: Optimization Parameter Discovery for Nested Module Params

**Decision**: Use the existing `[OptimizableModule]` attribute on module interface properties in `ModularStrategyParamsBase`. The `SpaceDescriptorBuilder` will recursively discover module variant params via `ModuleParamsBase` inheritance.

**Rationale**: The infrastructure already exists:
- `SpaceDescriptorBuilder.ScanProperties()` finds `[OptimizableModule]` on interface properties
- `DiscoverModuleVariants()` scans implementations with `[ModuleKey]`
- `FindModuleParamsType()` identifies constructor params inheriting `ModuleParamsBase`
- `ParameterScaler.ScaleAxes()` recurses into `ModuleSelection` values for `QuoteAsset` scaling

No optimizer changes needed. New module params just need to inherit `ModuleParamsBase` and carry `[Optimizable]` attributes.

**Key files**: `src/AlgoTradeForge.Infrastructure/Optimization/SpaceDescriptorBuilder.cs`, `src/AlgoTradeForge.Application/Optimization/ParameterScaler.cs`

## R4: Filter Module Interface Migration

**Decision**: Create a new `IFilterModule` interface with `int Evaluate(Int64Bar bar, OrderSide proposedSide)` returning [-100, +100]. Refactor existing `AtrVolatilityFilterModule` to implement it (returning 100 for allowed, 0 for blocked by ATR range).

**Rationale**: The existing `AtrVolatilityFilterModule.IsAllowed()` returns `bool`. The pipeline needs scored filters for weighted composition. The refactored module converts its ATR range check into a score while keeping backward compatibility (any code using `IsAllowed()` can call `Evaluate() >= 0` instead).

**Alternatives considered**:
- Keep `IsAllowed()` and wrap with adapter: rejected because it creates unnecessary indirection and the ATR filter is the only existing filter.
- Add both `IsAllowed()` and `Evaluate()`: rejected to avoid dual-interface confusion.

**Key file**: `src/AlgoTradeForge.Domain/Strategy/Modules/Filter/AtrVolatilityFilterModule.cs`

## R5: Live Connector Compatibility

**Decision**: `ModularStrategyBase` implements `ITradeRegistryProvider` to expose the trade registry for live reconciliation. No changes needed to `BinanceLiveConnector` or `OrderGroupReconciler`.

**Rationale**: The live connector already:
- Calls `OnBarComplete()` and `OnTrade()` via event queue serialization
- Uses `ITradeRegistryProvider.TradeRegistry.GetExpectedOrders()` for reconciliation
- Calls `RepairGroup()` for missing orders
The modular pipeline runs identically in backtest and live because it's triggered by the same `OnBarComplete()` callback.

**Key files**: `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceLiveConnector.cs`, `src/AlgoTradeForge.Domain/Strategy/Modules/TradeRegistry/ITradeRegistryProvider.cs`

## R6: Event System for Pipeline Observability

**Decision**: Add new event types for pipeline observability: `FilterEvaluationEvent` and `ExitEvaluationEvent`. Reuse existing `SignalEvent` (for signal generation), `OrderGroupEvent` (for trade registry state changes), and `RiskEvent` (for sizing decisions).

**Rationale**: The existing event system is rich (Signal, Risk, OrderGroup, Bar, Fill, Indicator events all exist). The pipeline only needs two new events to cover the filter gate and exit evaluation — all other decision points map to existing event types.

**Key files**: `src/AlgoTradeForge.Domain/Events/` (SignalEvents.cs, OrderEvents.cs, OrderGroupEvents.cs)

## R7: New Indicators Required

**Decision**: Implement RSI, SMA, and DonchianChannel as new `Int64IndicatorBase` subclasses. These are required by model strategies.

**Rationale**:
- RSI(2) model strategy needs: RSI indicator, SMA indicator (trend filter)
- Donchian Breakout needs: DonchianChannel indicator (upper/lower bands)
- ATR already exists
- These are standard technical indicators with well-defined formulas

**Alternatives considered**:
- Use double-based indicators instead of Int64: rejected for RSI and SMA (they operate on price data which is long-scaled). DonchianChannel operates on price (long). RSI output is a ratio (0-100) better represented as double, but since `Int64IndicatorBase` uses `long` buffers, RSI will store values as scaled integers (e.g., RSI 65.5 → 6550 with 2-decimal precision), consistent with the existing indicator convention.

Actually, re-evaluating: The user specified "for indicator values not requiring decimal precision (should be the vast majority) use double if not scaled to candle prices." RSI (0-100), SMA slope ratio, and similar non-price indicators should use double. But the existing `Int64IndicatorBase` uses `long` buffers.

**Revised decision**: Create a new `DoubleIndicatorBase` (or use the generic `IIndicator<Int64Bar, double>`) for indicators whose output is not price-scaled (RSI, ratios). Price-level indicators (SMA of price, DonchianChannel bands) continue using `Int64IndicatorBase` with `long` buffers.

## R8: Trailing Stop Per-Group State

**Decision**: `TrailingStopModule` maintains a `Dictionary<long, TrailingStopState>` keyed by order group ID. Each group gets independent stop tracking. Methods: `Activate(groupId, entry, direction, initialStop)`, `Update(groupId, bar)`, `GetCurrentStop(groupId)`, `Remove(groupId)`.

**Rationale**: Clarification Q2 confirmed per-group tracking within a single instance. A dictionary is O(1) lookup by group ID. State struct holds: current stop level, direction, activation price, variant-specific state (e.g., ATR period for ATR variant).

## R9: Regime Detection Algorithm

**Decision**: Use ADX-based regime classification as the default variant. ADX > threshold = Trending, ADX < threshold = RangeBound. Optionally combined with ATR percentile for Volatile detection.

**Rationale**: ADX is the standard regime detection indicator. It's simple, well-understood, and the Donchian model strategy needs trending-vs-range classification. The module publishes `MarketRegime` to `StrategyContext`.

**Alternatives considered**:
- HMM-based: too complex for initial implementation, violates YAGNI
- Bollinger bandwidth: measures volatility but not trend strength
- Custom: can be added as additional variants later

## R10: Model Strategy Placement

**Decision**: All three model strategies go in the public repo under `src/AlgoTradeForge.Domain/Strategy/` (like BuyAndHold). They're framework demonstrations, not proprietary strategies.

**Rationale**: Model strategies prove the framework works and run in public CI. They use standard textbook logic (RSI, Donchian, z-score) — no proprietary edge.
