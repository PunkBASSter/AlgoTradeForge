# Implementation Plan: Production-Grade Backtest Engine

**Branch**: `003-backtest-engine` | **Date**: 2026-02-14 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/003-backtest-engine/spec.md`

## Summary

Redesign the backtest engine from a single-subscription prototype into a production-grade, multi-subscription, event-driven system. The engine loads history for all strategy subscriptions via `IHistoryRepository` (with timeframe resampling), feeds bars chronologically using a k-way merge, and processes an in-memory order queue supporting Market, Limit, Stop, and StopLimit orders with SL/TP execution logic. Dead code (`StrategyAction`) is removed per constitution. *(Note: OrderTracker module deferred from this scope for redesign.)*

## Technical Context

**Language/Version**: C# 14 / .NET 10
**Primary Dependencies**: Existing solution dependencies (no new NuGet packages required for this feature)
**Storage**: In-memory `TimeSeries<Int64Bar>` loaded from CSV via existing `IInt64BarLoader`; no new storage
**Testing**: xUnit + NSubstitute (per constitution)
**Target Platform**: Windows (dev), Linux (production)
**Project Type**: Multi-project solution (clean architecture)
**Performance Goals**: 500K bars/min throughput (baseline SLA from spec); deterministic, reproducible results
**Constraints**: All data must fit in memory for a single backtest run; netting position model only; GTC orders only
**Scale/Scope**: 2-3 subscriptions per strategy; up to 1 year of 1-minute data per subscription (~525K bars each)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Strategy-as-Code | PASS | Strategy interface redesigned. Strategies remain self-contained C# code. No direct I/O — orders flow through `IOrderContext`. Analytical state (indicator buffers, bar history) permitted between bars per Constitution v1.3.0; all execution state MUST flow through `IOrderContext`. |
| II. Test-First | PASS | xUnit + NSubstitute. All new components (BarMatcher extensions, resampler, merge logic) designed for unit testability via interfaces. Backtest results are reproducible given identical inputs. |
| III. Data Integrity & Provenance | PASS | Historical data loaded read-only from CSV. Timestamps are UTC `DateTimeOffset`. Gap handling explicit (skip and continue). Resampled data is derived, not persisted. |
| IV. Observability & Auditability | PASS | Fill events record every order execution with full context (order ID, price, timestamp, side, commission, SL/TP flag). Rejection events logged. Structured logging for engine lifecycle (start, bar count, completion, duration). |
| V. Separation of Concerns | PASS | Domain: engine, strategy interface, order model, bar matcher — no Application references. Application: `IHistoryRepository`, backtest orchestration (handler pre-loads data and passes `TimeSeries<Int64Bar>[]` to engine). Infrastructure: history repository implementation. No UI, no persistence writes. |
| VI. Simplicity & YAGNI | PASS | Dead code (`StrategyAction`) deleted. No database, no streaming, no distributed coordination. Simple cursor-based merge for 2-3 subscriptions. GTC-only orders (no expiry engine). Netting-only positions. |
| Background Jobs | N/A | This feature is a synchronous engine, not a background job. |
| Test Framework | PASS | xUnit + NSubstitute. No FluentAssertions. |
| Code Style | PASS | File-scoped namespaces, `required` properties, `ct` for CancellationToken, no XML comments unless non-obvious. |
| Async/Concurrency | PASS | Engine loop is synchronous (in-memory data, no I/O during tick processing). Data loading is async via existing `IInt64BarLoader`. |

**Post-Phase 1 re-check**: All gates pass. No new projects needed — all changes fit within existing Domain, Application, and Infrastructure layers. No new external dependencies.

## Project Structure

### Documentation (this feature)

```text
specs/003-backtest-engine/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── checklists/
│   └── requirements.md  # Spec quality checklist
├── contracts/
│   ├── strategy-interface.md    # IIntBarStrategy + IOrderContext
│   ├── history-repository.md    # IHistoryRepository contract
│   └── (order-tracker.md removed — deferred from scope)
└── tasks.md             # Phase 2 output (from /speckit.tasks)
```

### Source Code (repository root)

```text
src/
├── AlgoTradeForge.Domain/
│   ├── Strategy/
│   │   ├── IIntBarStrategy.cs            # MODIFIED: new OnBar(Int64Bar, DataSubscription, IOrderContext) signature
│   │   ├── StrategyBase.cs               # MODIFIED: adapt to new interface
│   │   ├── StrategyParamsBase.cs          # UNCHANGED
│   │   ├── StrategyAction.cs             # DELETED (dead code, replaced by IOrderContext)
│   │   ├── DataSubscription.cs           # UNCHANGED
│   │   └── IOrderContext.cs              # NEW: order submission/cancellation/query interface
│   ├── Trading/
│   │   ├── Order.cs                      # MODIFIED: add StopPrice, StopLossPrice, TakeProfitLevels
│   │   ├── OrderType.cs                  # MODIFIED: add Stop, StopLimit
│   │   ├── OrderSide.cs                  # UNCHANGED
│   │   ├── OrderStatus.cs                # MODIFIED: add Rejected, Triggered
│   │   ├── Fill.cs                       # UNCHANGED
│   │   ├── TakeProfitLevel.cs            # NEW: price + closure percentage
│   │   ├── Portfolio.cs                  # UNCHANGED (netting already implemented)
│   │   ├── Position.cs                   # UNCHANGED
│   │   # IOrderTracker / OrderTracker / TrackedPosition / ClosedTrade — DEFERRED from scope
│   ├── Engine/
│   │   ├── BacktestEngine.cs             # MODIFIED: major rewrite — multi-sub merge, order queue, SL/TP
│   │   ├── BacktestOptions.cs            # MODIFIED: add UseDetailedExecutionLogic
│   │   ├── BacktestResult.cs             # UNCHANGED
│   │   ├── IBarMatcher.cs                # MODIFIED: expanded for Stop, StopLimit, SL/TP
│   │   ├── BarMatcher.cs                 # MODIFIED: handle all 4 order types + SL/TP evaluation
│   │   └── OrderQueue.cs                 # NEW: in-memory order queue with FIFO processing
│   └── History/
│       ├── Int64Bar.cs                   # UNCHANGED (already long from 002)
│       ├── TimeSeries.cs                 # UNCHANGED
│       └── TimeSeriesExtensions.cs        # NEW: Resample() extension method for timeframe upsampling
│
├── AlgoTradeForge.Application/
│   ├── Abstractions/
│   │   └── IHistoryRepository.cs         # NEW: Load(subscription, from, to) → TimeSeries<Int64Bar>
│   ├── Backtests/
│   │   ├── RunBacktestCommand.cs         # MODIFIED: multi-subscription support
│   │   └── RunBacktestCommandHandler.cs  # MODIFIED: use IHistoryRepository, feed engine
│   └── CandleIngestion/
│       └── IInt64BarLoader.cs            # UNCHANGED (wrapped by IHistoryRepository impl)
│
├── AlgoTradeForge.Infrastructure/
│   ├── History/
│   │   └── HistoryRepository.cs          # NEW: IHistoryRepository impl wrapping CsvInt64BarLoader + resampling
│   └── CandleIngestion/
│       └── CsvInt64BarLoader.cs          # UNCHANGED

tests/
├── AlgoTradeForge.Domain.Tests/
│   ├── Engine/
│   │   ├── BacktestEngineTests.cs        # MODIFIED: rewrite for multi-sub, order queue, SL/TP
│   │   ├── BarMatcherTests.cs            # MODIFIED: add Stop, StopLimit, SL/TP test cases
│   │   └── OrderQueueTests.cs            # NEW: FIFO ordering, fill/cancel lifecycle
│   ├── History/
│   │   └── TimeSeriesResampleTests.cs    # NEW: OHLCV aggregation correctness
│   ├── Trading/
│   │   └── PortfolioTests.cs             # EXISTING: may need updates for rejection scenarios
│   └── TestUtilities/
│       └── TestBars.cs                   # MODIFIED: add multi-subscription test data factories
│
└── AlgoTradeForge.Infrastructure.Tests/
    └── History/
        └── HistoryRepositoryTests.cs     # NEW: load + resample integration tests
```

**Structure Decision**: No new projects needed. All changes fit within the existing clean architecture layers: Domain (engine, strategy, trading, history), Application (IHistoryRepository abstraction), Infrastructure (repository implementation). Tests follow the existing mirror structure.

## Complexity Tracking

> No constitution violations requiring justification. All changes fit within existing project boundaries and use simple patterns (lists, cursors, enums).

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| (none) | — | — |
