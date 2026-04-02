# Implementation Plan: Strategy Module Framework

**Branch**: `027-strategy-module-framework` | **Date**: 2026-04-02 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/027-strategy-module-framework/spec.md`

## Summary

Build a modular strategy framework that provides a sealed three-phase bar-processing pipeline (Update Context → Manage Positions → Evaluate Entry) via `ModularStrategyBase<TParams>`. Strategy developers implement only signal generation; all other pipeline steps (filters, exits, trailing stops, sizing, order submission) are pre-built, composable modules. The framework extends existing `StrategyBase<TParams>`, reuses `TradeRegistryModule`, and requires no changes to the backtest engine, optimization engine, or live connector. Validated by three model strategies: RSI(2) mean-reversion, Donchian breakout, and pairs trading.

## Technical Context

**Language/Version**: C# 14 / .NET 10
**Primary Dependencies**: Existing AlgoTradeForge solution (Domain, Application, Infrastructure, WebApi). No new NuGet packages.
**Storage**: N/A — all new types are in-memory domain objects. No persistence changes.
**Testing**: xUnit + NSubstitute (existing stack)
**Target Platform**: Windows/Linux (existing backtest engine, live Binance connector)
**Project Type**: Extending existing clean architecture solution (Domain + tests primarily)
**Performance Goals**: <10% per-bar processing regression vs. equivalent hand-coded strategy (SC-006). `long` for prices, `double` for non-price indicators, `decimal` for money/volume.
**Constraints**: No shared mutable state between optimization trials. Null modules = zero overhead. Phase 2 skipped when flat.
**Scale/Scope**: ~30 new types (base class, params, context, 6 module implementations, 6 exit rules, 4 indicators, 2 event types, 3 model strategies with params). All in Domain layer + Domain.Tests.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
|-----------|--------|----------|
| I. Strategy-as-Code | PASS | `ModularStrategyBase` extends `StrategyBase` → implements `IInt64BarStrategy`. Self-contained, explicit indicators/params, all execution via `IOrderContext`. Analytical state (indicators, regime, trailing stop) maintained between bars per the "MAY maintain internal analytical state" clause. |
| II. Test-First | PASS | SC-007 requires 100% branch coverage on all modules. Model strategies tested via backtest. Tests written before implementation per constitution. |
| III. Data Integrity | PASS (N/A) | No data ingestion/storage changes. Uses existing `Int64Bar` and `IFeedContext`. |
| IV. Observability | PASS | FR-024 requires events at every decision point. New `FilterEvaluationEvent` and `ExitEvaluationEvent`. Reuses existing `SignalEvent`, `RiskEvent`, `OrderGroupEvent`. FR-025 requires debug probe observability. |
| V. Separation of Concerns | PASS | All new types in Domain layer (strategy modules). No API endpoints added. No frontend changes. Backtest engine, optimizer, live connector unchanged. |
| VI. Simplicity & YAGNI | PASS with justification | ~30 new types is significant but justified: (1) each type has single responsibility, (2) the framework eliminates duplicated pipeline code across every future strategy, (3) composition pattern preferred — modules are composed, only the base class uses inheritance, (4) all types are required by the spec's functional requirements (FR-006 through FR-032). See Complexity Tracking. |

**Post-Phase 1 re-check**: Constitution still passes. The data model introduces no additional abstraction layers beyond what the spec requires. No new projects, no new storage, no new external dependencies.

## Project Structure

### Documentation (this feature)

```text
specs/027-strategy-module-framework/
├── spec.md              # Feature specification
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # N/A — domain framework, no API endpoints
│   └── README.md        # Explanation of why no contracts
└── tasks.md             # Phase 2 output (via /speckit.tasks)
```

### Source Code (repository root)

```text
src/AlgoTradeForge.Domain/
├── Indicators/
│   ├── Atr.cs                              # Existing
│   ├── DeltaZigZag.cs                      # Existing
│   ├── DeltaZigZagTrend.cs                 # Existing
│   ├── DoubleIndicatorBase.cs              # NEW — base for non-price indicators (double buffers)
│   ├── Rsi.cs                              # NEW — RSI indicator (double output)
│   ├── Sma.cs                              # NEW — Simple Moving Average (long output)
│   ├── DonchianChannel.cs                  # NEW — Donchian Channel (long output)
│   └── Adx.cs                              # NEW — ADX indicator (double output)
├── Events/
│   ├── FilterEvaluationEvent.cs            # NEW
│   └── ExitEvaluationEvent.cs              # NEW
├── Strategy/
│   ├── Modules/
│   │   ├── IStrategyModule.cs              # Existing
│   │   ├── ModuleParamsBase.cs             # Existing
│   │   ├── ModularStrategyBase.cs          # NEW — sealed 3-phase pipeline
│   │   ├── ModularStrategyParamsBase.cs    # NEW — params with thresholds
│   │   ├── StrategyContext.cs              # NEW — per-bar shared state
│   │   ├── IFilterModule.cs               # NEW — scored filter interface
│   │   ├── Filter/
│   │   │   ├── AtrVolatilityFilterModule.cs  # MODIFIED — implement IFilterModule
│   │   │   └── RegimeFilterModule.cs         # NEW
│   │   ├── Exit/
│   │   │   ├── ExitModule.cs                 # NEW — exit rule aggregator
│   │   │   ├── IExitRule.cs                  # NEW — exit rule interface
│   │   │   ├── ExitParams.cs                 # NEW
│   │   │   ├── TimeBasedExitRule.cs          # NEW
│   │   │   ├── ProfitTargetExitRule.cs       # NEW
│   │   │   ├── SignalReversalExitRule.cs     # NEW
│   │   │   ├── RegimeChangeExitRule.cs       # NEW
│   │   │   ├── SessionCloseExitRule.cs       # NEW
│   │   │   └── CointegrationBreakExitRule.cs # NEW
│   │   ├── TrailingStop/
│   │   │   ├── TrailingStopModule.cs         # NEW — per-group trailing stop
│   │   │   ├── TrailingStopParams.cs         # NEW
│   │   │   ├── TrailingStopVariant.cs        # NEW — enum
│   │   │   └── TrailingStopState.cs          # NEW — per-group value type
│   │   ├── MoneyManagement/
│   │   │   ├── MoneyManagementModule.cs      # NEW
│   │   │   ├── MoneyManagementParams.cs      # NEW
│   │   │   └── SizingMethod.cs               # NEW — enum
│   │   ├── Regime/
│   │   │   ├── RegimeDetectorModule.cs       # NEW
│   │   │   ├── RegimeDetectorParams.cs       # NEW
│   │   │   └── MarketRegime.cs               # NEW — enum
│   │   ├── CrossAsset/
│   │   │   ├── CrossAssetModule.cs           # NEW
│   │   │   └── CrossAssetParams.cs           # NEW
│   │   └── TradeRegistry/                    # Existing — no changes
│   │       ├── TradeRegistryModule.cs
│   │       ├── OrderGroup.cs
│   │       └── ...
│   ├── Rsi2MeanReversion/
│   │   ├── Rsi2MeanReversionStrategy.cs      # NEW — model strategy
│   │   └── Rsi2Params.cs                     # NEW
│   ├── DonchianBreakout/
│   │   ├── DonchianBreakoutStrategy.cs       # NEW — model strategy
│   │   └── DonchianParams.cs                 # NEW
│   └── PairsTrading/
│       ├── PairsTradingStrategy.cs           # NEW — model strategy
│       └── PairsTradingParams.cs             # NEW

tests/AlgoTradeForge.Domain.Tests/
├── Strategy/
│   ├── Modules/
│   │   ├── ModularStrategyBaseTests.cs       # NEW — pipeline orchestration tests
│   │   ├── StrategyContextTests.cs           # NEW
│   │   ├── Filter/
│   │   │   ├── AtrVolatilityFilterModuleTests.cs  # MODIFIED — test new Evaluate()
│   │   │   └── RegimeFilterModuleTests.cs         # NEW
│   │   ├── Exit/
│   │   │   ├── ExitModuleTests.cs                 # NEW
│   │   │   ├── TimeBasedExitRuleTests.cs          # NEW
│   │   │   ├── ProfitTargetExitRuleTests.cs       # NEW
│   │   │   ├── SignalReversalExitRuleTests.cs     # NEW
│   │   │   └── RegimeChangeExitRuleTests.cs       # NEW
│   │   ├── TrailingStop/
│   │   │   └── TrailingStopModuleTests.cs         # NEW
│   │   ├── MoneyManagement/
│   │   │   └── MoneyManagementModuleTests.cs      # NEW
│   │   ├── Regime/
│   │   │   └── RegimeDetectorModuleTests.cs       # NEW
│   │   └── CrossAsset/
│   │       └── CrossAssetModuleTests.cs           # NEW
│   ├── Rsi2MeanReversionStrategyTests.cs     # NEW — backtest integration
│   ├── DonchianBreakoutStrategyTests.cs      # NEW
│   └── PairsTradingStrategyTests.cs          # NEW
├── Indicators/
│   ├── RsiTests.cs                           # NEW
│   ├── SmaTests.cs                           # NEW
│   ├── DonchianChannelTests.cs               # NEW
│   └── AdxTests.cs                           # NEW
```

**Structure Decision**: All new types go in the existing Domain project under `Strategy/Modules/` (framework) and `Strategy/{StrategyName}/` (model strategies). New indicators go in `Indicators/`. Tests mirror the source structure in `Domain.Tests`. No new projects are added.

## Complexity Tracking

| Aspect | Count | Justification |
|--------|-------|---------------|
| ~30 new types | Required by spec | Each maps 1:1 to a functional requirement (FR-006 through FR-032). No speculative types. |
| Inheritance (ModularStrategyBase) | Sealed pipeline pattern | Constitution prefers composition, but sealed pipeline inheritance is the standard Template Method pattern — it guarantees ordering. Modules within the pipeline use composition. |
| 6 exit rules | FR-009 requires them | Each is a small, independently testable class (~20 lines). No shared base — just `IExitRule` interface. |
| 4 new indicators | Model strategies need them | RSI, SMA, DonchianChannel, ADX are standard indicators that would be needed regardless of the framework. |
| DoubleIndicatorBase | Performance requirement | User specified "double for indicator values not requiring decimal precision." Existing `Int64IndicatorBase` uses `long` buffers. New base allows `double` buffers for RSI, ADX, etc. |
