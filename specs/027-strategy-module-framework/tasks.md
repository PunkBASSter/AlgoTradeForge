# Tasks: Strategy Module Framework

**Input**: Design documents from `/specs/027-strategy-module-framework/`
**Prerequisites**: plan.md, spec.md, data-model.md, research.md, quickstart.md

**Tests**: Included — Constitution Principle II (Test-First) requires tests before implementation. SC-007 requires 100% branch coverage on all modules.

**Organization**: Tasks grouped by user story in priority order. US6/US7 (exit rules, sizing) are pulled ahead of US2/US3 because those model strategies depend on them.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: Create directory structure and foundational types that are trivial but block everything

- [x] T001 Create module subdirectories under `src/AlgoTradeForge.Domain/Strategy/Modules/` — Exit/, TrailingStop/, MoneyManagement/, Regime/, CrossAsset/ — and mirror in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/`
- [x] T002 [P] Create `MarketRegime` enum (Unknown, Trending, RangeBound, HighVolatility) in `src/AlgoTradeForge.Domain/Strategy/Modules/Regime/MarketRegime.cs`
- [x] T003 [P] Create `SizingMethod` enum (FixedFractional, AtrVolTarget, HalfKelly) in `src/AlgoTradeForge.Domain/Strategy/Modules/MoneyManagement/SizingMethod.cs`
- [x] T004 [P] Create `TrailingStopVariant` enum (Atr, Chandelier, Donchian) in `src/AlgoTradeForge.Domain/Strategy/Modules/TrailingStop/TrailingStopVariant.cs`
- [x] T005 [P] Create `DoubleIndicatorBase` abstract class (implements `IIndicator<Int64Bar, double>`, double-typed `IndicatorBuffer` outputs) in `src/AlgoTradeForge.Domain/Indicators/DoubleIndicatorBase.cs` — mirrors `Int64IndicatorBase` but with `double` buffers for non-price indicators (RSI, ADX)

---

## Phase 2: Foundational — Core Pipeline & Interfaces

**Purpose**: Core framework types that ALL user stories depend on. No user story can begin until this phase completes.

**CRITICAL**: No user story work can begin until this phase is complete.

### Tests (write first, must fail)

- [x] T006 [P] Write tests for `StrategyContext` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/StrategyContextTests.cs` — test `Update()` populates bar/equity/cash, `Set<T>`/`Get<T>`/`Has()` key-value store, `CurrentRegime` defaults to Unknown, null-safe `Get<T>` returns default
- [x] T007 [P] Write tests for `ExitModule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/ExitModuleTests.cs` — test empty rules returns 0, single rule returns its score, multiple rules returns most negative, -100 always wins
- [x] T008 [P] Write tests for `MoneyManagementModule` (FixedFractional only) in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/MoneyManagement/MoneyManagementModuleTests.cs` — test `CalculateSize()` with known entry/SL/equity, quantity rounded down to asset lot step, clamped to min/max, returns 0 when position too small
- [x] T009 Write tests for `ModularStrategyBase` pipeline orchestration in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/ModularStrategyBaseTests.cs` — test 3-phase execution order, Phase 2 skipped when flat, Phase 3 short-circuits at each gate (capacity, filter, signal threshold, SL validation, min qty), secondary subscription bars skip Phases 2-3, filter weighted averaging, `OnTrade` routes through trade registry before `OnOrderFilled` hook

### Implementation

- [x] T010 [P] Implement `StrategyContext` in `src/AlgoTradeForge.Domain/Strategy/Modules/StrategyContext.cs` — per-bar state container with CurrentBar, CurrentSubscription, Equity, Cash, CurrentRegime, CurrentAtr, CurrentVolatility, key-value store (Dictionary<string, object>), `Update()` method
- [x] T011 [P] Implement `IFilterModule` interface in `src/AlgoTradeForge.Domain/Strategy/Modules/IFilterModule.cs` — extends `IStrategyModule`, methods: `Initialize(IIndicatorFactory, DataSubscription)`, `Evaluate(Int64Bar, OrderSide) → int` returning [-100, +100]
- [x] T012 [P] Implement `IExitRule` interface in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/IExitRule.cs` — property: `Name`, method: `Evaluate(Int64Bar, StrategyContext, OrderGroup) → int` returning [-100, +100]
- [x] T013 [P] Implement `ExitModule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/ExitModule.cs` — aggregates `List<IExitRule>`, `AddRule()`, `Evaluate()` returns most negative score (min-wins), 0 when no rules
- [x] T014 [P] Implement `ExitParams` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/ExitParams.cs` — extends `ModuleParamsBase`, placeholder for future exit-level configuration
- [x] T015 [P] Implement `MoneyManagementParams` in `src/AlgoTradeForge.Domain/Strategy/Modules/MoneyManagement/MoneyManagementParams.cs` — extends `ModuleParamsBase`, fields: `Method` (SizingMethod, default FixedFractional), `RiskPercent` (double, [Optimizable] Min=0.5/Max=5.0/Step=0.5, default 1.0), `VolTarget`, `WinRate`, `PayoffRatio`
- [x] T016 Implement `MoneyManagementModule` (FixedFractional path) in `src/AlgoTradeForge.Domain/Strategy/Modules/MoneyManagement/MoneyManagementModule.cs` — `CalculateSize(entryPrice, stopLoss, context, asset) → decimal`: fixed-fractional = (equity * riskPercent) / |entry - SL|, round down via `asset.RoundQuantityDown()`, clamp to min/max, return 0 if below min. Emit `RiskEvent` via context. Stub AtrVolTarget and HalfKelly branches (throw NotImplementedException for now).
- [x] T017 [P] Implement `FilterEvaluationEvent` in `src/AlgoTradeForge.Domain/Events/FilterEvaluationEvent.cs` — record implementing `IBacktestEvent`, fields: Timestamp, Source, AssetName, FilterScores (Dictionary<string, int>), CompositeScore, Passed. TypeId = "filter.eval", ExportMode = Backtest | Live
- [x] T018 [P] Implement `ExitEvaluationEvent` in `src/AlgoTradeForge.Domain/Events/ExitEvaluationEvent.cs` — record implementing `IBacktestEvent`, fields: Timestamp, Source, AssetName, GroupId, RuleScores (Dictionary<string, int>), CompositeScore, ExitTriggered. TypeId = "exit.eval", ExportMode = Backtest | Live
- [x] T019 Implement `ModularStrategyParamsBase` in `src/AlgoTradeForge.Domain/Strategy/Modules/ModularStrategyParamsBase.cs` — extends `StrategyParamsBase`, fields: FilterThreshold (int, [Optimizable] -50..50 step 10, default 0), SignalThreshold (int, [Optimizable] 10..80 step 10, default 30), ExitThreshold (int, [Optimizable] -100..-20 step 10, default -50), DefaultAtrStopMultiplier (double, [Optimizable] 1.0..5.0 step 0.5, default 2.0), MoneyManagement (MoneyManagementParams), TradeRegistry (TradeRegistryParams), TrailingStop?, Exit?, RegimeDetector?, FilterWeights dictionary, `GetFilterWeight()` method
- [x] T020 Implement `ModularStrategyBase<TParams>` in `src/AlgoTradeForge.Domain/Strategy/Modules/ModularStrategyBase.cs` — extends `StrategyBase<TParams>` where `TParams : ModularStrategyParamsBase`, implements `ITradeRegistryProvider`. Sealed `OnInit()` (creates Context, instantiates TradeRegistry + MoneyManagement from params, calls `OnStrategyInit()`), sealed `OnBarComplete()` (3-phase pipeline per spec), sealed `OnTrade()` (routes fill to trade registry then `OnOrderFilled()`). Phase 1: update context + regime detector + `OnContextUpdated()`. Phase 2: iterate active groups → trailing stop update + exit evaluation + custom exit + close/update-SL decision. Phase 3: capacity check → filter gate → `OnGenerateSignal()` → `OnGetEntryPrice()` → `OnGetRiskLevels()` → SL validation → sizing → `OnExecuteEntry()`. Multi-sub: check if subscription is primary (index 0) for Phases 2-3. Emit FilterEvaluationEvent, ExitEvaluationEvent, SignalEvent at decision points. Abstract: `OnGenerateSignal()`. Virtual defaults: `OnGetEntryPrice()` → (0, Market), `OnGetRiskLevels()` → ATR-based from context, `OnExecuteEntry()` → single order via TradeRegistry, `OnEvaluateExit()` → 0, `OnGetExitPrice()` → 0, `OnStrategyInit()`, `OnContextUpdated()`, `OnOrderFilled()`.

**Checkpoint**: Core pipeline compilable and tests passing. Strategies can now inherit from `ModularStrategyBase`.

---

## Phase 3: US1 — Build a Minimal Strategy (RSI2 Mean-Reversion) (Priority: P1) MVP

**Goal**: Prove the framework works for a single-override strategy. RSI(2) overrides only `OnGenerateSignal()` and registers one filter.

**Independent Test**: Run RSI(2) backtest → entries placed on RSI oversold/overbought + trend filter, stop-loss from default ATR, sizing from FixedFractional.

### Tests (write first)

- [x] T021 [P] [US1] Write tests for `Rsi` indicator in `tests/AlgoTradeForge.Domain.Tests/Indicators/RsiTests.cs` — test warmup period requirement, RSI(2) on known price series produces expected values (verify against known RSI formula), values bounded 0-100, double output precision
- [x] T022 [P] [US1] Write tests for `Sma` indicator in `tests/AlgoTradeForge.Domain.Tests/Indicators/SmaTests.cs` — test SMA on known series (e.g., [10,20,30,40,50] period=3 → [NaN,NaN,20,30,40]), warmup period, long output
- [x] T023 [P] [US1] Write tests for refactored `AtrVolatilityFilterModule.Evaluate()` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Filter/AtrVolatilityFilterModuleTests.cs` — update existing tests for new `int Evaluate()` return type (100 when ATR in range, 0 when out of range), test `Initialize()` creates ATR indicator
- [x] T024 [US1] Write integration test for `Rsi2MeanReversionStrategy` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Rsi2MeanReversionStrategyTests.cs` — create strategy with test params, feed bars via `BacktestEngine.Run()`, assert: entries on RSI < OversoldThreshold when price > SMA, stop-loss placed at entry - ATR * multiplier, position sized by FixedFractional, filter blocks when ATR out of range, pipeline emits SignalEvent and FilterEvaluationEvent

### Implementation

- [x] T025 [P] [US1] Implement `Rsi` indicator in `src/AlgoTradeForge.Domain/Indicators/Rsi.cs` — extends `DoubleIndicatorBase`, single buffer "Value" (double, 0-100), Wilder's smoothed RSI formula, configurable period, `Measure = IndicatorMeasure.MinusOnePlusOne` (convention for bounded 0-100)
- [x] T026 [P] [US1] Implement `Sma` indicator in `src/AlgoTradeForge.Domain/Indicators/Sma.cs` — extends `Int64IndicatorBase`, single buffer "Value" (long, price-scaled), simple moving average of Close prices, configurable period
- [x] T027 [US1] Refactor `AtrVolatilityFilterModule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Filter/AtrVolatilityFilterModule.cs` — implement `IFilterModule` interface, change `IsAllowed(bar, side) → bool` to `Evaluate(bar, side) → int` returning 100 (ATR in range) or 0 (out of range). Keep `[ModuleKey("filter.atr-volatility")]`. Update `Initialize()` to match `IFilterModule.Initialize()` signature if needed.
- [x] T028 [P] [US1] Create `Rsi2Params` in `src/AlgoTradeForge.Domain/Strategy/Rsi2MeanReversion/Rsi2Params.cs` — extends `ModularStrategyParamsBase`, fields: RsiPeriod (int, [Optimizable] 2..14 step 1, default 2), OversoldThreshold (double, default 10), OverboughtThreshold (double, default 90), TrendFilterPeriod (int, [Optimizable] 50..200 step 25, default 200), AtrFilter (AtrVolatilityFilterParams)
- [x] T029 [US1] Implement `Rsi2MeanReversionStrategy` in `src/AlgoTradeForge.Domain/Strategy/Rsi2MeanReversion/Rsi2MeanReversionStrategy.cs` — `[StrategyKey("RSI2-MeanReversion")]`, extends `ModularStrategyBase<Rsi2Params>`. `OnStrategyInit()`: create RSI + SMA + ATR indicators via `Indicators.Create()`, register `AtrVolatilityFilterModule`, write ATR to context in `OnContextUpdated()`. `OnGenerateSignal()`: RSI < oversold + Close > SMA → Buy 80, RSI > overbought + Close < SMA → Sell 80, else 0.

**Checkpoint**: RSI(2) strategy runs in backtest, enters/exits positions, uses filters, sizes correctly. US1 independently testable.

---

## Phase 4: US6 — Exit Rule Building Blocks (Priority: P2)

**Goal**: Pre-built exit rules that can be composed without custom code. Most-negative-score-wins aggregation.

**Independent Test**: Configure exit module with time-based + profit-target rules, run backtest, verify positions close on whichever rule fires first.

### Tests (write first)

- [x] T030 [P] [US6] Write tests for `TimeBasedExitRule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/TimeBasedExitRuleTests.cs` — returns 0 before MaxHoldBars, returns -100 at MaxHoldBars, tracks bars since entry via OrderGroup.CreatedAt
- [x] T031 [P] [US6] Write tests for `ProfitTargetExitRule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/ProfitTargetExitRuleTests.cs` — returns 0 when PnL < target, returns -60 when PnL >= N*ATR (uses context.CurrentAtr)
- [x] T032 [P] [US6] Write tests for `SignalReversalExitRule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/SignalReversalExitRuleTests.cs` — returns -70 when signal direction flipped vs entry side, 0 when same direction or no signal
- [x] T033 [P] [US6] Write tests for `RegimeChangeExitRule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/RegimeChangeExitRuleTests.cs` — returns -80 when context.CurrentRegime differs from entry regime, 0 when same, 0 when Unknown

### Implementation

- [x] T034 [P] [US6] Implement `TimeBasedExitRule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/TimeBasedExitRule.cs` — `IExitRule`, computes bars held from group.CreatedAt and current bar timestamp, returns -100 when exceeded
- [x] T035 [P] [US6] Implement `ProfitTargetExitRule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/ProfitTargetExitRule.cs` — `IExitRule`, computes unrealized PnL from group entry vs current price, compares against AtrMultiple * context.CurrentAtr
- [x] T036 [P] [US6] Implement `SignalReversalExitRule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/SignalReversalExitRule.cs` — `IExitRule`, takes a `Func<Int64Bar, StrategyContext, (int signal, OrderSide side)>` delegate (bound to strategy's signal logic), returns -70 when sign flipped
- [x] T037 [P] [US6] Implement `RegimeChangeExitRule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/RegimeChangeExitRule.cs` — `IExitRule`, stores entry regime when activated, returns -80 when context.CurrentRegime != entryRegime, 0 when Unknown

**Checkpoint**: Exit rules independently testable. ExitModule composes them correctly.

---

## Phase 5: US7 — Position Sizing Methods (Priority: P2)

**Goal**: Swap sizing method via params without code changes. FixedFractional already done in Phase 2.

**Independent Test**: Run same strategy with each sizing method, verify quantities differ according to method formula.

### Tests (write first)

- [x] T038 [P] [US7] Write tests for `MoneyManagementModule` AtrVolTarget path in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/MoneyManagement/MoneyManagementModuleTests.cs` — test qty = (equity * volTarget) / (ATR * multiplier), inversely proportional to ATR
- [x] T039 [P] [US7] Write tests for `MoneyManagementModule` HalfKelly path in same test file — test qty = 0.5 * f(winRate, payoffRatio) * equity / price, Kelly fraction formula: f = (winRate * payoffRatio - (1 - winRate)) / payoffRatio

### Implementation

- [x] T040 [US7] Complete `MoneyManagementModule` — implement AtrVolTarget and HalfKelly branches in `src/AlgoTradeForge.Domain/Strategy/Modules/MoneyManagement/MoneyManagementModule.cs`, replace `NotImplementedException` stubs from T016

**Checkpoint**: All 3 sizing methods work. Interchangeable via params.

---

## Phase 6: US2 — Donchian Breakout Strategy (Priority: P2)

**Goal**: Prove virtual override points work (entry price, risk levels), plus trailing stop, regime detector, and regime filter compose correctly.

**Independent Test**: Run Donchian backtest → stop orders at channel, trailing stop ratchets, regime filter blocks range-bound entries, regime-change exit closes positions.

### Tests (write first)

- [x] T041 [P] [US2] Write tests for `DonchianChannel` indicator in `tests/AlgoTradeForge.Domain.Tests/Indicators/DonchianChannelTests.cs` — test upper = max(high, period), lower = min(low, period), middle = (upper+lower)/2, warmup period, long output
- [x] T042 [P] [US2] Write tests for `Adx` indicator in `tests/AlgoTradeForge.Domain.Tests/Indicators/AdxTests.cs` — test ADX on known series, values 0-100, warmup = 2*period, double output
- [x] T043 [P] [US2] Write tests for `TrailingStopModule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/TrailingStop/TrailingStopModuleTests.cs` — test per-group state: Activate() creates entry, Update() ratchets stop only in favorable direction (long: up only, short: down only), GetCurrentStop() returns correct per-group value, Remove() cleans up, multiple concurrent groups tracked independently, ATR variant ratchets by ATR distance
- [x] T044 [P] [US2] Write tests for `RegimeDetectorModule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Regime/RegimeDetectorModuleTests.cs` — test Update() classifies Trending when ADX > threshold, RangeBound when below, writes to context.CurrentRegime, returns Unknown during warmup
- [x] T045 [P] [US2] Write tests for `RegimeFilterModule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Filter/RegimeFilterModuleTests.cs` — test returns 100 when regime in allowed set, -100 when not, 0 when Unknown
- [x] T046 [US2] Write integration test for `DonchianBreakoutStrategy` in `tests/AlgoTradeForge.Domain.Tests/Strategy/DonchianBreakoutStrategyTests.cs` — run backtest, assert: stop orders at Donchian channel boundaries, custom ATR-based risk levels, trailing stop ratchets on favorable moves, regime filter blocks range-bound entries, regime-change exit fires

### Implementation

- [x] T047 [P] [US2] Implement `DonchianChannel` indicator in `src/AlgoTradeForge.Domain/Indicators/DonchianChannel.cs` — extends `Int64IndicatorBase`, 3 buffers: "Upper" (max high over period), "Lower" (min low over period), "Middle" (midpoint, long), configurable period
- [x] T048 [P] [US2] Implement `Adx` indicator in `src/AlgoTradeForge.Domain/Indicators/Adx.cs` — extends `DoubleIndicatorBase`, single buffer "Value" (double, 0-100), standard ADX formula (DI+, DI-, DX, smoothed ADX), configurable period, `Measure = IndicatorMeasure.MinusOnePlusOne`
- [x] T049 [P] [US2] Implement `TrailingStopParams` in `src/AlgoTradeForge.Domain/Strategy/Modules/TrailingStop/TrailingStopParams.cs` — extends `ModuleParamsBase`, fields: Variant (TrailingStopVariant), AtrMultiplier, AtrPeriod, DonchianPeriod with [Optimizable] attributes per data-model
- [x] T050 [P] [US2] Implement `TrailingStopState` value type in `src/AlgoTradeForge.Domain/Strategy/Modules/TrailingStop/TrailingStopState.cs` — record struct with CurrentStop, Direction, ActivationPrice, HighWaterMark
- [x] T051 [US2] Implement `TrailingStopModule` in `src/AlgoTradeForge.Domain/Strategy/Modules/TrailingStop/TrailingStopModule.cs` — `IStrategyModule<TrailingStopParams>`, `[ModuleKey("trailing-stop")]`, internal `Dictionary<long, TrailingStopState>` keyed by group ID. Methods: `Activate(groupId, entryPrice, direction, initialStop)`, `Update(groupId, bar) → long?` (ratchet only in favorable direction based on variant), `GetCurrentStop(groupId)`, `Remove(groupId)`. ATR variant: stop = highWaterMark - atrMultiplier * ATR.
- [x] T052 [P] [US2] Implement `RegimeDetectorParams` in `src/AlgoTradeForge.Domain/Strategy/Modules/Regime/RegimeDetectorParams.cs` — extends `ModuleParamsBase`, fields: AdxPeriod, TrendThreshold per data-model
- [x] T053 [US2] Implement `RegimeDetectorModule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Regime/RegimeDetectorModule.cs` — `IStrategyModule<RegimeDetectorParams>`, `[ModuleKey("regime-detector")]`. `Initialize()` creates ADX indicator. `Update(bar, context)`: compute ADX → if warmup return Unknown, if ADX > threshold → Trending, else → RangeBound. Write to `context.CurrentRegime`.
- [x] T054 [US2] Implement `RegimeFilterModule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Filter/RegimeFilterModule.cs` — `IFilterModule`, `[ModuleKey("filter.regime")]`. Constructor takes `MarketRegime[]` allowedRegimes. `Evaluate()`: reads context.CurrentRegime, returns 100 if in allowed set, -100 if not, 0 if Unknown.
- [x] T055 [P] [US2] Create `DonchianParams` in `src/AlgoTradeForge.Domain/Strategy/DonchianBreakout/DonchianParams.cs` — extends `ModularStrategyParamsBase`, fields: EntryPeriod, ExitPeriod, AtrPeriod, AtrStopMultiplier with [Optimizable] attributes. Includes TrailingStop and RegimeDetector nested params.
- [x] T056 [US2] Implement `DonchianBreakoutStrategy` in `src/AlgoTradeForge.Domain/Strategy/DonchianBreakout/DonchianBreakoutStrategy.cs` — `[StrategyKey("DonchianBreakout")]`, extends `ModularStrategyBase<DonchianParams>`. `OnStrategyInit()`: create DonchianChannel (entry+exit), ATR indicators, register TrailingStopModule, RegimeDetectorModule, RegimeFilter (allow Trending only), RegimeChangeExitRule. `OnGenerateSignal()`: breakout above/below previous bar's channel. `OnGetEntryPrice()`: stop order at channel boundary. `OnGetRiskLevels()`: SL at entry ∓ ATR * multiplier. `OnContextUpdated()`: write ATR to context. Activate trailing stop in `OnOrderFilled()`.

**Checkpoint**: Donchian strategy runs in backtest with custom entry/exit, trailing stop, regime filtering. US2 independently testable.

---

## Phase 7: US3 — Pairs Trading Strategy (Priority: P3)

**Goal**: Prove multi-asset, multi-leg, custom execution, cross-asset modules, and cointegration-break exit all work together.

**Independent Test**: Run pairs backtest with 2 subscriptions → z-score entries fire both legs, cointegration break exits both, secondary bars update context without triggering trades.

### Tests (write first)

- [x] T057 [P] [US3] Write tests for `CrossAssetModule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/CrossAsset/CrossAssetModuleTests.cs` — test z-score calculation (spread mean/stddev), hedge ratio computation, cointegration status flag, writes to context via keys "crossasset.zscore", "crossasset.hedge_ratio", "crossasset.cointegrated"
- [x] T058 [P] [US3] Write tests for `CointegrationBreakExitRule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/CointegrationBreakExitRuleTests.cs` — returns -100 when context "crossasset.cointegrated" is false, 0 when true
- [x] T059 [P] [US3] Write tests for `SessionCloseExitRule` in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/Exit/SessionCloseExitRuleTests.cs` — returns -100 when current UTC hour matches CloseHourUtc, 0 otherwise
- [x] T060 [US3] Write integration test for `PairsTradingStrategy` in `tests/AlgoTradeForge.Domain.Tests/Strategy/PairsTradingStrategyTests.cs` — run backtest with 2 assets, assert: both legs entered on z-score crossing entry threshold, both legs exited on z-score reversion past exit threshold, cointegration break exits both legs immediately, secondary subscription bars update cross-asset module but don't trigger Phase 2-3

### Implementation

- [x] T061 [P] [US3] Implement `CrossAssetParams` in `src/AlgoTradeForge.Domain/Strategy/Modules/CrossAsset/CrossAssetParams.cs` — extends `ModuleParamsBase`, fields: LookbackPeriod, ZScoreEntryThreshold, ZScoreExitThreshold per data-model
- [x] T062 [US3] Implement `CrossAssetModule` in `src/AlgoTradeForge.Domain/Strategy/Modules/CrossAsset/CrossAssetModule.cs` — `IStrategyModule<CrossAssetParams>`, `[ModuleKey("cross-asset")]`. `Initialize(factory, sub1, sub2)` sets up rolling window. `Update(bar, sub, context)`: collect close prices per subscription into rolling windows, compute spread = log(A) - hedgeRatio*log(B), z-score = (spread - mean) / stddev, simplified cointegration check (correlation threshold + spread stationarity), write to context keys.
- [x] T063 [P] [US3] Implement `CointegrationBreakExitRule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/CointegrationBreakExitRule.cs` — `IExitRule`, reads `context.Get<bool>("crossasset.cointegrated")`, returns -100 when false
- [x] T064 [P] [US3] Implement `SessionCloseExitRule` in `src/AlgoTradeForge.Domain/Strategy/Modules/Exit/SessionCloseExitRule.cs` — `IExitRule`, checks bar timestamp UTC hour against configured CloseHourUtc
- [x] T065 [P] [US3] Create `PairsTradingParams` in `src/AlgoTradeForge.Domain/Strategy/PairsTrading/PairsTradingParams.cs` — extends `ModularStrategyParamsBase`, nested CrossAssetParams. Two DataSubscriptions required.
- [x] T066 [US3] Implement `PairsTradingStrategy` in `src/AlgoTradeForge.Domain/Strategy/PairsTrading/PairsTradingStrategy.cs` — `[StrategyKey("PairsTrading")]`, extends `ModularStrategyBase<PairsTradingParams>`. `OnStrategyInit()`: create CrossAssetModule, register CointegrationBreakExitRule. `OnGenerateSignal()`: z-score threshold check from context. `OnGetRiskLevels()`: SL at extreme z-score (3*ATR). `OnExecuteEntry()`: submit BOTH legs (buy A + sell B or inverse) with hedge ratio. `OnEvaluateExit()`: z-score reversion check + cointegration break.

**Checkpoint**: Pairs strategy runs in backtest with 2 assets. US3 independently testable.

---

## Phase 8: US4 — Optimization Compatibility (Priority: P2)

**Goal**: Verify existing optimizer discovers all modular strategy parameters including nested module params.

**Independent Test**: Run optimization evaluate endpoint on RSI(2) strategy, verify all [Optimizable] params discovered.

- [x] T067 [US4] Write optimization parameter discovery test in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/ModularStrategyParamsOptimizationTests.cs` — use `SpaceDescriptorBuilder` to scan `Rsi2Params`, assert it discovers: top-level params (FilterThreshold, SignalThreshold, ExitThreshold, DefaultAtrStopMultiplier, RsiPeriod, etc.), nested MoneyManagement params (Method, RiskPercent), nested TradeRegistry params (MaxConcurrentGroups). Verify `ParameterScaler` scales QuoteAsset params correctly.
- [x] T068 [US4] Write parallel trial isolation test in same file — create two `Rsi2MeanReversionStrategy` instances with different params, run backtests in parallel (Task.WhenAll), assert no shared state (different Context instances, different indicator instances, different trade registry state)

**Checkpoint**: Optimization compatibility proven.

---

## Phase 9: US5 — Live Binance Connector Compatibility (Priority: P3)

**Goal**: Verify `ModularStrategyBase` implements `ITradeRegistryProvider` correctly for live reconciliation.

**Independent Test**: Verify `ITradeRegistryProvider.TradeRegistry` returns the module's trade registry, `GetExpectedOrders()` works through modular base.

- [x] T069 [US5] Write `ITradeRegistryProvider` implementation test in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/ModularStrategyBaseLiveTests.cs` — verify `ModularStrategyBase` cast to `ITradeRegistryProvider` succeeds, `TradeRegistry` property returns the trade registry module, `GetExpectedOrders()` returns correct protective orders after entry, fill routing updates order group state correctly
- [x] T070 [US5] Write reconciliation flow test in same file — simulate: entry → fill → trailing stop update → SL modification, verify trade registry state after each step, verify `GetExpectedOrders()` reflects updated SL price, verify `RepairGroup()` resubmits missing SL

**Checkpoint**: Live connector compatibility verified. ModularStrategyBase works identically in backtest and live.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Performance validation, observability verification, documentation

- [x] T071 [P] Write performance benchmark test in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/ModularStrategyPerformanceTests.cs` — [Trait("Category", "Performance")], benchmark: run RSI(2) modular vs. equivalent hand-coded BuyAndHold-style strategy over 10K bars, assert <10% per-bar time regression (SC-006). Verify null modules add zero overhead (no filter vs. with filter).
- [x] T072 [P] Write event emission verification test in `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/ModularStrategyEventTests.cs` — run RSI(2) with mock IEventBus, assert FilterEvaluationEvent emitted on each Phase 3 execution, SignalEvent emitted on entry, ExitEvaluationEvent emitted on Phase 2, RiskEvent emitted on sizing
- [x] T073 [P] Write debug probe observability test in same file — run with mock IDebugProbe, assert `OnBarProcessed()` called after each bar, verify DebugSnapshot contains correct sequence numbers
- [x] T074 Update `docs/strategy-framework.md` status to reflect implementation complete, add notes on any deviations from the original design document during implementation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 (enums, DoubleIndicatorBase) — **BLOCKS all user stories**
- **US1 (Phase 3)**: Depends on Phase 2 completion
- **US6 (Phase 4)**: Depends on Phase 2. Can run in parallel with US1.
- **US7 (Phase 5)**: Depends on Phase 2. Can run in parallel with US1/US6.
- **US2 (Phase 6)**: Depends on Phase 2 + Phase 4 (needs RegimeChangeExitRule from US6)
- **US3 (Phase 7)**: Depends on Phase 2 + Phase 4 (needs CointegrationBreakExitRule)
- **US4 (Phase 8)**: Depends on US1 completion (needs a real strategy to test)
- **US5 (Phase 9)**: Depends on US1 completion (needs a real strategy to test)
- **Polish (Phase 10)**: Depends on US1 + US2 completion at minimum

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2 — no dependencies on other stories
- **US6 (P2)**: Can start after Phase 2 — no dependencies on other stories (exit rules are standalone)
- **US7 (P2)**: Can start after Phase 2 — no dependencies on other stories (sizing methods standalone)
- **US2 (P2)**: Needs RegimeChangeExitRule from US6 Phase 4; otherwise independent
- **US3 (P3)**: Needs CointegrationBreakExitRule (can be built in US3 phase directly if US6 not done)
- **US4 (P2)**: Needs a working model strategy (US1)
- **US5 (P3)**: Needs a working model strategy (US1)

### Parallel Opportunities

Within Phase 2: T006-T009 (tests) all parallel, T010-T018 (implementations) mostly parallel
Within Phase 3: T021-T023 (tests) parallel, T025-T026 (indicators) parallel, T028 parallel with indicators
Within Phase 4: T030-T033 (tests) all parallel, T034-T037 (implementations) all parallel
Within Phase 6: T041-T045 (tests) all parallel, T047-T052 (implementations) mostly parallel
Within Phase 7: T057-T059 (tests) all parallel, T061-T065 (implementations) mostly parallel

Across phases (after Phase 2): US1, US6, US7 can all proceed in parallel.

---

## Parallel Example: Phase 2 Foundation

```bash
# Launch all foundation tests in parallel (write first, must fail):
Task: T006 "StrategyContext tests"
Task: T007 "ExitModule tests"
Task: T008 "MoneyManagement tests"

# After tests written, launch parallel implementations:
Task: T010 "StrategyContext"
Task: T011 "IFilterModule"
Task: T012 "IExitRule"
Task: T013 "ExitModule"
Task: T014 "ExitParams"
Task: T015 "MoneyManagementParams"
Task: T017 "FilterEvaluationEvent"
Task: T018 "ExitEvaluationEvent"

# Then sequential (depends on above):
Task: T016 "MoneyManagementModule"
Task: T019 "ModularStrategyParamsBase"
Task: T020 "ModularStrategyBase"
```

## Parallel Example: US1 + US6 + US7 (after Phase 2)

```bash
# These three story phases can run in parallel:
# Stream A: US1 (RSI2 strategy)
Task: T021-T029

# Stream B: US6 (Exit rules)
Task: T030-T037

# Stream C: US7 (Sizing methods)
Task: T038-T040
```

---

## Implementation Strategy

### MVP First (US1 Only)

1. Complete Phase 1: Setup (T001-T005)
2. Complete Phase 2: Foundational (T006-T020)
3. Complete Phase 3: US1 — RSI(2) (T021-T029)
4. **STOP and VALIDATE**: Run `dotnet test` on RSI(2) tests. Verify backtest produces entries/exits.
5. At this point the framework is proven for single-override strategies.

### Incremental Delivery

1. Setup + Foundational → Core pipeline ready
2. US1 → RSI(2) working → **MVP! Framework proven.**
3. US6 + US7 → Exit rules + sizing methods → Building blocks catalog ready
4. US2 → Donchian working → Overrides, trailing stop, regime proven
5. US3 → Pairs working → Multi-asset, custom execution proven
6. US4 + US5 → Optimization + live compat verified
7. Polish → Performance validated, events verified, docs updated

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Constitution Principle II requires tests BEFORE implementation (Red-Green-Refactor)
- All price computations use `long`. Indicator outputs: `double` for RSI/ADX, `long` for SMA/DonchianChannel.
- `decimal` only for money management quantity output (compatible with `Asset.RoundQuantityDown(decimal)`)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
