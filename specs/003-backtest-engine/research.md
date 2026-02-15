# Research: Production-Grade Backtest Engine

**Branch**: `003-backtest-engine` | **Date**: 2026-02-14

## R-001: Strategy Interface Redesign

**Decision**: Replace `IIntBarStrategy.OnBarComplete(TimeSeries<Int64Bar>)` with a new method signature that receives a single `Int64Bar`, a `DataSubscription` identifier, and an `IOrderContext` for order submission.

**Rationale**: The spec requires live-like conditions where the strategy sees only the latest completed bar (FR-003). The current interface passes the full `TimeSeries<Int64Bar>`, which exposes all bars including future data during iteration. The `StrategyAction` return-value pattern is dead code and doesn't support pending orders. The new interface injects an `IOrderContext` so the strategy can submit, cancel, and observe orders without coupling to the engine.

**Alternatives considered**:
- Keep `TimeSeries<Int64Bar>` but truncate to current index → rejected because it still allocates the full series and the strategy could access `Count` to infer remaining data
- Return `IEnumerable<Order>` from `OnBarComplete` → rejected because it doesn't support order cancellation or fill observation; also couples strategy to return semantics

**Impact**: Breaking change to `IIntBarStrategy`. `StrategyBase<TParams>` updated. `StrategyAction` deleted. All existing strategy implementations must adapt (currently only test mocks).

## R-002: Order Queue Architecture

**Decision**: In-memory `List<Order>` managed by the engine. Strategy submits orders via `IOrderContext.Submit(Order)` during bar events. Engine processes the queue after each bar delivery using FIFO order per asset.

**Rationale**: The queue is internal to a single backtest run — no persistence, no concurrency, no distributed coordination. A simple list is sufficient. The engine iterates the queue on each bar tick, evaluating pending orders against the current bar. Filled or rejected orders are removed; remaining orders persist (GTC per FR-024).

**Alternatives considered**:
- `Channel<Order>` (async producer-consumer) → rejected as over-engineered for synchronous backtest loop
- `PriorityQueue<Order>` by price → rejected because FIFO submission order is required (FR-015), not price priority
- Separate queue per asset → considered acceptable optimization for later; start with single queue filtered by asset

**Impact**: New `IOrderContext` interface in Domain. Engine loop restructured: deliver bar → strategy callback → process order queue → generate fills → update portfolio.

## R-003: Extended Order Model

**Decision**: Extend the existing `Order` class with `StopPrice` (trigger for Stop/StopLimit), `StopLossPrice`, and `TakeProfitPrices` (list for multiple TP levels with partial closure percentages). Add `Stop` and `StopLimit` to `OrderType` enum. Add `Rejected` and `Triggered` to `OrderStatus` enum.

**Rationale**: The spec requires four order types (FR-005) and SL/TP awareness (FR-011). The existing `Order` class has `LimitPrice` for Limit orders; extending it with `StopPrice` for Stop/StopLimit trigger and SL/TP fields keeps a single order entity rather than introducing subclasses.

**Alternatives considered**:
- Inheritance hierarchy (`MarketOrder`, `LimitOrder`, `StopOrder`) → rejected because it complicates the queue (heterogeneous collection), pattern matching overhead, and the BarMatcher already switches on `OrderType`
- Separate `StopLoss` and `TakeProfit` entities linked to an order → rejected as premature normalization; SL/TP are order attributes, not independent entities

**Impact**: `Order` class gains new optional properties. `OrderType` enum expanded. `OrderStatus` enum expanded. `BarMatcher` must handle new types.

## R-004: SL/TP Execution Logic

**Decision**: After an order is filled, the engine evaluates SL/TP levels against each subsequent bar. When a bar's range covers both SL and TP, the default behavior is worst-case (SL hit first). An optional `UseDetailedExecutionLogic` flag loads lower-timeframe data from `IHistoryRepository` to determine the actual sequence.

**Rationale**: Worst-case default prevents false positives in backtest results (FR-012). The detailed execution mode (FR-013) is opt-in because lower-timeframe data may not be available and loading it has a performance cost. Falling back to worst-case when data is unavailable (FR-014) ensures determinism.

**Alternatives considered**:
- Always assume TP hit first (optimistic) → rejected as dangerous for production parity
- Require lower-timeframe data (no fallback) → rejected because users may not have 1m data for all assets
- Probabilistic fill based on bar position → rejected as non-deterministic and hard to test

**Impact**: `BarMatcher` expanded with SL/TP evaluation methods returning `hitTpIndex` out parameter (`< 0` = SL, `>= 0` = TP level index). `FillReason` enum removed as redundant. `BacktestOptions` gains `UseDetailedExecutionLogic` flag. When detailed mode is enabled, the handler pre-loads lower-timeframe data (asset's `SmallestInterval`, typically 1m) as `Dictionary<Asset, TimeSeries<Int64Bar>>` and passes it to the engine as auxiliary data — NOT delivered to the strategy via `OnBar`. The engine MUST NOT reference `IHistoryRepository` directly (Domain cannot depend on Application). Warnings (e.g., fallback to worst-case) are surfaced via the `BacktestResult` rather than requiring `ILogger` injection into the Domain layer.

## R-005: IHistoryRepository Design

**Decision**: New `IHistoryRepository` interface in the Application layer. Single method: `Load(DataSubscription subscription, DateOnly from, DateOnly to)` returning `TimeSeries<Int64Bar>`. Implementation wraps `IInt64BarLoader` and adds timeframe resampling.

**Rationale**: The engine needs a single entry point to load data for any subscription, including resampled timeframes (FR-004, FR-022). Placing it in Application follows the existing pattern where `IInt64BarLoader` lives. The implementation handles: resolving the data root and exchange/symbol from the subscription's asset, loading raw 1m data, and resampling to the requested timeframe.

**Alternatives considered**:
- Engine calls `IInt64BarLoader` directly + separate resampler → rejected because it pushes resampling responsibility to the engine, violating separation of concerns
- Put resampling in Domain → rejected because resampling depends on data loading configuration (DataRoot), which is infrastructure

**Impact**: New interface in Application. New implementation in Infrastructure. The `RunBacktestCommandHandler` (Application) uses `IHistoryRepository` to pre-load all subscription data and passes `TimeSeries<Int64Bar>[]` to the engine. The engine (Domain) MUST NOT depend on `IHistoryRepository` — it receives pre-loaded data only, preserving clean architecture layering (Domain has no Application references).

## ~~R-006: Order Tracking Module Design~~ (DEFERRED)

> Removed from current scope for redesign. The original decision placed `IOrderTracker` and `OrderTracker` in Domain/Trading with netting model and partial TP closure. This will be re-evaluated in a future iteration.

## R-007: Bar Resampling Strategy

**Decision**: Resample at load time inside `IHistoryRepository`, not during the feed loop. Standard OHLCV aggregation: first Open, max High, min Low, last Close, sum Volume. Resampled data is materialized into `TimeSeries<Int64Bar>` once per subscription per backtest run.

**Rationale**: Resampling during the feed loop would require buffering partial bars and tracking bar boundaries per subscription — complex state management for each tick. Loading and resampling upfront is simpler, the data fits in memory (a year of 5m bars is ~105K entries), and there's no performance benefit to lazy resampling since all data will eventually be consumed.

**Alternatives considered**:
- Lazy resampling during iteration → rejected due to complexity (partial bar accumulation, flush-on-boundary logic) with no meaningful benefit for in-memory data
- Store pre-resampled files on disk → rejected per YAGNI; 1m data is the canonical source, resampling on-the-fly is fast

**Impact**: `IHistoryRepository` implementation includes a `Resample` method. The engine receives already-resampled `TimeSeries<Int64Bar>` per subscription.

## R-008: Dead Code Cleanup

**Decision**: Delete `StrategyAction.cs` and its factory methods (`MarketBuy`, `MarketSell`, `LimitBuy`, `LimitSell`). Remove the `pendingAction` variable from `BacktestEngine`.

**Rationale**: `StrategyAction` is dead code — it's defined but the engine's current loop discards the concept. The new order queue replaces it entirely. Constitution Principle VI (Simplicity & YAGNI) mandates deleting dead code immediately.

**Impact**: `StrategyAction.cs` deleted. No consumers exist beyond the prototype engine loop (which is being rewritten).

## R-009: Chronological Multi-Subscription Merge

**Decision**: After loading all subscription data into separate `TimeSeries<Int64Bar>` collections, the engine builds a merged event stream by maintaining a cursor (index) per subscription. On each tick, it finds the subscription(s) with the smallest next timestamp, advances that cursor, and delivers the bar. Ties broken by subscription declaration order.

**Rationale**: This is the classic k-way merge of sorted sequences. Since each subscription's data is already sorted (ascending timestamps from CSV), a simple min-heap or linear scan of cursors is efficient. For 2-3 subscriptions, linear scan is simpler and equally performant.

**Alternatives considered**:
- Interleave all bars into one sorted list upfront → rejected because it loses subscription identity and wastes memory duplicating data
- `SortedDictionary<DateTimeOffset, List<(int subIdx, Int64Bar bar)>>` → rejected as over-engineered for 2-3 subscriptions; the cursor approach is O(k) per tick where k is small

**Impact**: Engine maintains `int[]` cursors and `TimeSeries<Int64Bar>[]` per subscription. Main loop picks the minimum timestamp, delivers bar, advances cursor.
