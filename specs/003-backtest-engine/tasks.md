# Tasks: Production-Grade Backtest Engine

**Input**: Design documents from `/specs/003-backtest-engine/`
**Prerequisites**: plan.md (required), spec.md, research.md, data-model.md, contracts/

**Tests**: Included per Constitution Principle II (Test-First). Tests are written before implementation within each phase.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3, US4)
- Include exact file paths in descriptions

## Path Conventions

- **Source**: `src/AlgoTradeForge.<Layer>/`
- **Tests**: `tests/AlgoTradeForge.<Layer>.Tests/`
- Domain tests mirror source folder structure

---

## Phase 1: Setup (Dead Code Cleanup & Strategy Interface)

**Purpose**: Remove dead code and establish the new strategy interface contract that all user stories depend on

- [x] T001 Delete dead code `src/AlgoTradeForge.Domain/Strategy/StrategyAction.cs`
- [x] T002 Update `IIntBarStrategy` with new `OnBar(Int64Bar, DataSubscription, IOrderContext)` signature in `src/AlgoTradeForge.Domain/Strategy/IIntBarStrategy.cs` (per `contracts/strategy-interface.md`)
- [x] T003 Create `IOrderContext` interface in `src/AlgoTradeForge.Domain/Strategy/IOrderContext.cs` (per `contracts/strategy-interface.md`)
- [x] T004 Update `StrategyBase<TParams>` to implement new `OnBar` abstract method in `src/AlgoTradeForge.Domain/Strategy/StrategyBase.cs`

---

## Phase 2: Foundational (Shared Domain Types)

**Purpose**: Extend order model and create shared types that multiple user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T005 [P] Add `Stop` and `StopLimit` values to `OrderType` enum in `src/AlgoTradeForge.Domain/Trading/OrderType.cs`
- [x] T006 [P] Add `Triggered` and `Cancelled` values to `OrderStatus` enum (`Rejected` already exists) in `src/AlgoTradeForge.Domain/Trading/OrderStatus.cs`
- [x] ~~T007 [P] Create `FillReason` enum — REMOVED: redundant with `hitTpIndex` out parameter in `BarMatcher.EvaluateSlTp`~~
- [x] T008 [P] Create `TakeProfitLevel` readonly record struct (`Price`, `ClosurePercentage`) in `src/AlgoTradeForge.Domain/Trading/TakeProfitLevel.cs`
- [x] T009 Extend `Order` class with `StopPrice`, `StopLossPrice`, and `TakeProfitLevels` properties in `src/AlgoTradeForge.Domain/Trading/Order.cs`
- [x] ~~T010 Extend `Fill` record with `Reason` property — REMOVED with FillReason~~
- [x] T011 Add `UseDetailedExecutionLogic` property (default `false`) to `BacktestOptions` in `src/AlgoTradeForge.Domain/Engine/BacktestOptions.cs`
- [x] T012 Update `TestBars` with multi-subscription test data factories (multiple assets, different timeframes) in `tests/AlgoTradeForge.Domain.Tests/TestUtilities/TestBars.cs`
- [x] T013 Verify solution builds with no errors after foundational changes (`dotnet build AlgoTradeForge.slnx`)

**Checkpoint**: All shared types in place. User story implementation can begin.

---

## Phase 3: User Story 1 - Multi-Subscription Data Loading & Chronological Bar Feeding (Priority: P1) 🎯 MVP

**Goal**: Engine loads history for all strategy subscriptions, resamples if needed, and feeds bars chronologically one at a time

**Independent Test**: Configure a strategy with two subscriptions (BTCUSDT 1m, ETHUSDT 5m), run a backtest, verify bars arrive in strict chronological order with correct subscription attribution

### Tests for User Story 1

> **Write these tests FIRST, ensure they FAIL before implementation**

- [x] T014 [P] [US1] Create `BarResamplerTests` with OHLCV aggregation correctness tests (1m→5m, verify Open/High/Low/Close/Volume) in `tests/AlgoTradeForge.Domain.Tests/History/BarResamplerTests.cs`
- [x] T015 [P] [US1] Create `HistoryRepositoryTests` with load + resample integration tests (mock `IInt64BarLoader`, verify resampled output) in `tests/AlgoTradeForge.Infrastructure.Tests/History/HistoryRepositoryTests.cs`
- [x] T016 [US1] Add multi-subscription chronological feeding tests to `BacktestEngineTests` — verify bar delivery order across 2 subscriptions, same-timestamp tiebreaker, data gap handling in `tests/AlgoTradeForge.Domain.Tests/Engine/BacktestEngineTests.cs`

### Implementation for User Story 1

- [x] T017 [US1] Create `BarResampler` static class with `Resample(TimeSeries<Int64Bar>, TimeSpan source, TimeSpan target)` method in `src/AlgoTradeForge.Domain/History/BarResampler.cs`
- [x] T018 [US1] Create `IHistoryRepository` interface with `Load(DataSubscription, DateOnly, DateOnly)` method in `src/AlgoTradeForge.Application/Abstractions/IHistoryRepository.cs` (per `contracts/history-repository.md`)
- [x] T019 [US1] Implement `HistoryRepository` wrapping `CsvInt64BarLoader` with resampling support in `src/AlgoTradeForge.Infrastructure/History/HistoryRepository.cs`
- [x] T020 [US1] Rewrite `BacktestEngine.Run` to accept pre-loaded `TimeSeries<Int64Bar>[]` and `DataSubscription[]`, maintain `int[]` cursors, pick minimum timestamp, deliver single bar via `strategy.OnBar()` — engine MUST NOT reference `IHistoryRepository` (data loading is the handler's responsibility) in `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs`
- [x] T021 [US1] Verify all US1 tests pass (`dotnet test --filter "BarResampler|HistoryRepository|BacktestEngine"`)

**Checkpoint**: Engine loads multi-subscription data, resamples, and feeds bars chronologically. Strategy receives one bar at a time.

---

## Phase 4: User Story 2 - Event-Driven Strategy & In-Memory Order Queue (Priority: P2)

**Goal**: Strategy places orders via `IOrderContext`, engine processes order queue each tick, supports Market/Limit/Stop/StopLimit

**Independent Test**: Write a strategy that places a Stop order, run a backtest, verify the order sits pending until its trigger price is hit, then fills correctly

### Tests for User Story 2

> **Write these tests FIRST, ensure they FAIL before implementation**

- [x] T022 [P] [US2] Create `OrderQueueTests` — FIFO ordering, submit/cancel lifecycle, `GetPendingForAsset` filtering, remove on fill, GTC persistence (verify pending order survives 100+ bar ticks without expiry per FR-024) in `tests/AlgoTradeForge.Domain.Tests/Engine/OrderQueueTests.cs`
- [x] T023 [P] [US2] Add Stop and StopLimit test cases to `BarMatcherTests` — Stop Buy/Sell trigger+fill, StopLimit trigger→Limit conversion, slippage on Stop fills in `tests/AlgoTradeForge.Domain.Tests/Engine/BarMatcherTests.cs`
- [x] T024 [US2] Add order queue integration tests to `BacktestEngineTests` — strategy submits Market/Limit/Stop/StopLimit orders, engine processes queue, fills generated with correct prices and timestamps, fill events observable via `IOrderContext.GetFills()` in `tests/AlgoTradeForge.Domain.Tests/Engine/BacktestEngineTests.cs`

### Implementation for User Story 2

- [x] T025 [US2] Create `OrderQueue` class with `Submit`, `Cancel`, `GetPendingForAsset`, `GetAll`, `Remove` methods in `src/AlgoTradeForge.Domain/Engine/OrderQueue.cs`
- [x] T026 [US2] Extend `IBarMatcher` interface and `BarMatcher` implementation with Stop and StopLimit fill logic (trigger detection, StopLimit→Limit conversion, slippage) in `src/AlgoTradeForge.Domain/Engine/IBarMatcher.cs` and `src/AlgoTradeForge.Domain/Engine/BarMatcher.cs`
- [x] T027 [US2] Implement `BacktestOrderContext` (concrete `IOrderContext`) that delegates to `OrderQueue` and maintains fill list, in `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs` (nested class or separate file)
- [x] T028 [US2] Integrate order queue into engine tick loop — after `strategy.OnBar()`, process pending orders via `BarMatcher`, generate fills, update portfolio, handle insufficient funds rejection (FR-023) in `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs`
- [x] T029 [US2] Verify all US2 tests pass (`dotnet test --filter "OrderQueue|BarMatcher|BacktestEngine"`)

**Checkpoint**: Strategy places orders via IOrderContext. Engine processes all 4 order types. Fills generated and observable.

---

## Phase 5: User Story 3 - Pending Order Execution with SL/TP Fill Logic (Priority: P3)

**Goal**: Engine evaluates SL/TP levels on filled orders, worst-case default for ambiguous bars, optional detailed execution mode

**Independent Test**: Place a Buy Stop with SL=95 and TP=110, verify worst-case (SL hit) when both are in bar range; verify TP-only and SL-only scenarios produce correct outcomes

### Tests for User Story 3

> **Write these tests FIRST, ensure they FAIL before implementation**

- [x] T030 [P] [US3] Add SL/TP test cases to `BarMatcherTests` — Buy with SL-only hit, TP-only hit, both in range (worst-case), Sell mirror cases, zero-range bar edge case in `tests/AlgoTradeForge.Domain.Tests/Engine/BarMatcherTests.cs`
- [x] T031 [P] [US3] Add detailed execution logic tests to `BarMatcherTests` — ambiguous bar with lower-timeframe data resolving SL-first vs TP-first, fallback to worst-case when data unavailable in `tests/AlgoTradeForge.Domain.Tests/Engine/BarMatcherTests.cs`
- [x] T032 [US3] Add SL/TP integration tests to `BacktestEngineTests` — end-to-end backtest where order fills, SL triggers on subsequent bar, portfolio updated; TP partial closure scenario in `tests/AlgoTradeForge.Domain.Tests/Engine/BacktestEngineTests.cs`

### Implementation for User Story 3

- [x] T033 [US3] Add SL/TP evaluation methods to `BarMatcher` — after entry fill, evaluate SL/TP against each bar, determine if SL or TP triggered via `hitTpIndex` out parameter, generate appropriate fill in `src/AlgoTradeForge.Domain/Engine/BarMatcher.cs`
- [x] T034 [US3] Implement worst-case default logic — when bar range covers both SL and TP, assume SL hit first (loss for the strategy) in `src/AlgoTradeForge.Domain/Engine/BarMatcher.cs`
- [x] T035 [US3] Implement detailed execution logic — when `UseDetailedExecutionLogic` is enabled, use pre-loaded lower-timeframe `TimeSeries<Int64Bar>` (passed by handler) to resolve ambiguous SL/TP order; fall back to worst-case with logged warning when data unavailable. BarMatcher MUST NOT reference `IHistoryRepository` in `src/AlgoTradeForge.Domain/Engine/BarMatcher.cs`
- [x] T036 [US3] Integrate SL/TP evaluation into engine fill processing loop — after entry fill, track SL/TP levels, evaluate on each subsequent bar, generate SL/TP fills, close position accordingly in `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs`
- [x] T037 [US3] Verify all US3 tests pass (`dotnet test --filter "BarMatcher|BacktestEngine"`)

**Checkpoint**: SL/TP execution logic fully functional. Worst-case default and detailed execution mode both working.

---

## ~~Phase 6: User Story 4 - Order Tracking Module for Production Parity (Priority: P4)~~ **DEFERRED**

> **Removed from current scope.** OrderTracker, IOrderTracker, TrackedPosition, ClosedTrade, and OrderTrackerTests deleted. Will be redesigned in a future iteration.
>
> Original tasks T038–T044 are no longer applicable.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Integration, application layer updates, performance validation

- [x] T045 Update `RunBacktestCommand` for multi-subscription support in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommand.cs`
- [x] T046 Update `RunBacktestCommandHandler` to use `IHistoryRepository` to pre-load all subscription data (and lower-timeframe data when `UseDetailedExecutionLogic` is enabled), then pass `TimeSeries<Int64Bar>[]` arrays to the engine in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`
- [x] T047 Verify full solution builds with no errors or warnings (`dotnet build AlgoTradeForge.slnx`)
- [x] T048 Verify all tests pass across entire solution (`dotnet test AlgoTradeForge.slnx`)
- [x] T049 Performance validation: run benchmark matching SC-007 (500K bars/min baseline) and document results
- [x] T050 Run quickstart.md validation steps end-to-end
- [x] T051 [US2] Add SC-005 100-order stress test — verify no queue corruption, lost orders, or duplicate fills with 100 orders in a single run in `tests/AlgoTradeForge.Domain.Tests/Engine/BacktestEngineTests.cs`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Foundational — can start after Phase 2
- **US2 (Phase 4)**: Depends on US1 — needs the multi-sub engine loop to integrate order queue
- **US3 (Phase 5)**: Depends on US2 — needs order execution pipeline for SL/TP evaluation
- **~~US4 (Phase 6)~~**: DEFERRED — removed from current scope for redesign
- **Polish (Phase 7)**: Depends on US1/US2/US3 being complete

### User Story Dependencies

```
Phase 1 (Setup) → Phase 2 (Foundational)
                        │
                        └──→ US1 (P1) → US2 (P2) → US3 (P3) → Phase 7 (Polish)
```

- **US1 → US2**: US2's order queue integrates into US1's engine loop
- **US2 → US3**: US3's SL/TP logic extends US2's fill processing

### Within Each User Story

- Tests MUST be written and FAIL before implementation
- Domain types before services/engine integration
- Core implementation before edge case handling
- Story complete before moving to next priority (unless parallelizing US4)

### Parallel Opportunities

- **Phase 2**: T005, T006, T007, T008 can all run in parallel (different files)
- **US1**: T014 and T015 (tests) can run in parallel
- **US2**: T022 and T023 (tests) can run in parallel
- **US3**: T030 and T031 (tests) can run in parallel
---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001–T004)
2. Complete Phase 2: Foundational (T005–T013)
3. Complete Phase 3: User Story 1 (T014–T021)
4. **STOP and VALIDATE**: Test multi-subscription chronological bar feeding independently
5. Deploy/demo if ready — engine can load data and feed bars, even without orders

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. Add US1 → Test: bars fed chronologically → **MVP!**
3. Add US2 → Test: all 4 order types execute correctly → Backtest with orders
4. Add US3 → Test: SL/TP produce correct P&L → Realistic backtesting
5. Polish → Performance validated, application layer integrated

### Parallel Team Strategy

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Constitution Principle II requires test-first: write failing tests before implementation
- Each user story checkpoint includes a test verification step
- `StrategyAction` deletion (T001) may cause temporary build failures — resolve in T002–T004
- The `BacktestEngine` is progressively rewritten: US1 adds multi-sub merge, US2 adds order queue, US3 adds SL/TP
- US4 (`OrderTracker`) has been **deferred** from current scope for redesign
