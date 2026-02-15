# Feature Specification: Production-Grade Backtest Engine

**Feature Branch**: `003-backtest-engine`
**Created**: 2026-02-14
**Status**: Implemented
**Input**: User description: "Production-grade backtest engine with multi-subscription support, event-driven strategy interface, pending orders with SL/TP, and order tracking module for production parity"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Multi-Subscription Data Loading & Chronological Bar Feeding (Priority: P1)

As a strategy developer, I want the backtest engine to load historical data for all of my strategy's data subscriptions and feed bars chronologically one at a time so that my strategy experiences the same conditions it would in live trading — receiving only the latest completed bar without access to future data.

**Why this priority**: Without the ability to load and feed multi-subscription data chronologically, no backtest can run. This is the foundational capability that replaces the current single-subscription prototype and enables all other features.

**Independent Test**: Can be fully tested by configuring a strategy with two subscriptions (e.g., BTCUSDT 1m and ETHUSDT 5m), running a backtest over a known date range, and verifying that the strategy receives bars in strict chronological order with correct subscription attribution, one bar at a time.

**Acceptance Scenarios**:

1. **Given** a strategy with a single subscription (BTCUSDT 1m) and stored history from 2025-01-01 to 2025-01-31, **When** a backtest runs over that date range, **Then** the engine loads all 1-minute bars via the history repository and delivers each completed bar to the strategy individually in ascending timestamp order.

2. **Given** a strategy with two subscriptions (BTCUSDT 1m, ETHUSDT 1m), **When** the engine feeds bars for a time range where both assets have data, **Then** bars are delivered in global chronological order (smallest timestamp first); when two bars share the same timestamp, they are delivered in the order the subscriptions are declared in the strategy.

3. **Given** a strategy with a subscription requesting 5-minute bars but only 1-minute data is stored, **When** the engine loads data, **Then** it resamples the 1-minute bars into 5-minute bars (aggregating open/high/low/close/volume correctly) before feeding them to the strategy.

4. **Given** a strategy with two subscriptions where one asset has a gap in data (missing bars for a period), **When** the engine feeds bars, **Then** it skips the gap for that subscription and continues feeding available bars without error, while the other subscription's bars continue to be delivered normally.

5. **Given** a strategy receives a bar event, **When** it inspects the bar, **Then** it sees only the single newly completed `Int64Bar` and its associated subscription identifier — not the full time series, not any future bars.

---

### User Story 2 - Event-Driven Strategy Interface & In-Memory Order Queue (Priority: P2)

As a strategy developer, I want to place orders (Market, Limit, Stop, StopLimit) through a simple order submission interface during bar events so that my strategy logic is cleanly separated from execution, and I can express complex trading intentions beyond just market orders.

**Why this priority**: The current prototype uses a dead-code `StrategyAction` pattern that supports only Market and Limit. Replacing it with an in-memory order queue that the engine processes each tick enables pending orders (Stop, StopLimit) and is the prerequisite for SL/TP execution logic.

**Independent Test**: Can be tested by writing a strategy that places a Stop order when a condition is met, running a backtest, and verifying the order sits in the queue until its trigger price is hit, then executes.

**Acceptance Scenarios**:

1. **Given** a strategy that places a Market Buy order on receiving a bar, **When** the next bar arrives, **Then** the engine fills the order at the next bar's open price (plus slippage) and notifies the strategy of the fill.

2. **Given** a strategy that places a Limit Buy order at a price below the current market, **When** a subsequent bar's low reaches or goes below the limit price, **Then** the order is filled at the limit price.

3. **Given** a strategy that places a Stop Buy order at a price above the current market, **When** a subsequent bar's high reaches or exceeds the stop price, **Then** the order is triggered and filled at the stop price (plus slippage).

4. **Given** a strategy that places a StopLimit Buy order (stop trigger at 100, limit at 102), **When** a bar's high reaches 100 (triggering the stop), **Then** the order becomes a Limit Buy at 102 and is filled when a subsequent bar's price reaches the limit.

5. **Given** a strategy places multiple orders across different subscriptions, **When** the engine processes a bar tick, **Then** only orders associated with that subscription's asset are evaluated for execution against the current bar.

6. **Given** a strategy that places an order, **When** the order is filled, **Then** the engine generates a fill event that the strategy can observe, including fill price, quantity, timestamp, and commission.

---

### User Story 3 - Pending Order Execution with SL/TP Fill Logic (Priority: P3)

As a strategy developer, I want the backtest engine to correctly simulate order execution including stop-loss and take-profit levels so that my backtest results reflect realistic worst-case scenarios and I can trust the P&L numbers.

**Why this priority**: Accurate SL/TP execution is critical for realistic backtesting. Without it, strategies may appear profitable in backtests but fail in production due to optimistic fill assumptions. This builds on the order queue from P2.

**Independent Test**: Can be tested by placing a Buy Stop order with SL and TP levels, running bars where only the SL is touched (but not TP), and verifying the engine reports a loss — and vice versa for TP-only scenarios.

**Acceptance Scenarios**:

1. **Given** a pending Buy Stop order with SL at 95 and TP at 110, and the order is triggered at 100, **When** the same or a subsequent bar's range covers both 95 (SL) and 110 (TP), and detailed execution logic is disabled, **Then** the engine assumes worst-case: the SL is hit first (loss).

2. **Given** a pending Buy Stop order with SL at 95 and TP at 110, triggered at 100, **When** a bar's range reaches 110 (TP) but never touches 95 (SL), **Then** the TP is filled and the trade is closed at a profit.

3. **Given** a pending Buy Stop order with SL at 95 and TP at 110, triggered at 100, **When** a bar's range reaches 95 (SL) but never touches 110 (TP), **Then** the SL is filled and the trade is closed at a loss.

4. **Given** a pending Sell Stop order with SL at 110 and TP at 90, triggered at 100, **When** a bar's range covers both 110 (SL) and 90 (TP), and detailed execution logic is disabled, **Then** the engine assumes worst-case: the SL is hit first (loss).

5. **Given** detailed execution logic is enabled and lower-timeframe data (e.g., 1-minute bars) is available, **When** an ambiguous bar occurs where both SL and TP are within range, **Then** the engine examines the lower-timeframe bars to determine which level was reached first and fills accordingly.

6. **Given** detailed execution logic is enabled but lower-timeframe data is not available for the ambiguous period, **When** the engine encounters a bar where both SL and TP are within range, **Then** it falls back to worst-case assumption (SL hit first) and logs a warning.

---

### ~~User Story 4 - Order Tracking Module for Production Parity (Priority: P4)~~ **DEFERRED**

> Removed from current scope for redesign. OrderTracker, IOrderTracker, TrackedPosition, and ClosedTrade will be re-specified in a future iteration.

---

### Edge Cases

- What happens when a bar's range is zero (open == high == low == close) and a pending order's trigger price exactly equals the bar price? The order is triggered and filled at that price.
- What happens when multiple pending orders for the same asset trigger on the same bar? Orders are processed in submission order (FIFO). Each fill updates the portfolio before the next order is evaluated.
- What happens when a Stop order is placed with a trigger price already within the current bar's range (stale stop)? The order is triggered on the next bar, not retroactively. Strategies must place orders based on completed bar data.
- What happens when a StopLimit order is triggered but the limit price is never reached? The order remains as a pending Limit order until filled or cancelled by the strategy.
- What happens when the strategy submits an order for an asset that has no remaining data? The order remains pending and is never filled. The engine does not error.
- What happens when the strategy places an order during a bar event for a subscription different from the one that triggered the event? The order is accepted and queued for processing when bars for that subscription's asset arrive.
- What happens when detailed execution logic is enabled but the lower-timeframe data only partially covers the ambiguous period? The engine uses available lower-timeframe data where possible and falls back to worst-case assumption for gaps.
- ~~What happens when the tracking module receives a fill event for an order it is not tracking (e.g., manually placed)? The module ignores untracked fill events and logs a warning.~~ *(Deferred with US4)*
- What happens when the strategy cancels a pending order? The order transitions to Cancelled status and is removed from the queue. Partial fills are not supported in the current model — orders are either fully filled or not filled.
- What happens when a fill would cause negative cash (insufficient funds)? The order is rejected and the strategy receives a rejection event. No implicit margin or partial fills are supported.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST load historical bar data for all strategy subscriptions via a history repository interface, supporting multiple assets and timeframes in a single backtest run
- **FR-002**: System MUST feed bars to the strategy in global chronological order (ascending timestamp), using subscription declaration order as a tiebreaker when bars share the same timestamp
- **FR-003**: System MUST deliver exactly one completed `Int64Bar` per event to the strategy, along with subscription identification, without exposing future bars or the full time series
- **FR-004**: System MUST resample stored lower-timeframe bars into requested higher timeframes when the exact timeframe is not available in storage (e.g., aggregate 1m bars into 5m bars)
- **FR-005**: System MUST support four order types: Market, Limit, Stop, and StopLimit
- **FR-006**: System MUST maintain an in-memory order queue where strategies submit orders during bar events, with the engine processing the queue on each bar tick
- **FR-007**: System MUST fill Market orders at the next bar's open price with configurable slippage
- **FR-008**: System MUST fill Limit orders when the bar's price range reaches the limit price
- **FR-009**: System MUST trigger Stop orders when the bar's price range reaches the stop trigger price and fill at the trigger price with slippage
- **FR-010**: System MUST convert triggered StopLimit orders into pending Limit orders when the stop trigger price is reached
- **FR-011**: System MUST evaluate pending order SL/TP levels against each bar and determine fill outcomes
- **FR-012**: System MUST assume worst-case fill order (SL hit before TP) when a single bar's range covers both SL and TP levels and detailed execution logic is not available
- **FR-013**: System MUST support a configurable detailed execution mode that uses lower-timeframe data (the asset's `SmallestInterval`, typically 1-minute bars) to resolve ambiguous SL/TP scenarios where both levels fall within a single bar's range. When `UseDetailedExecutionLogic` is enabled, the application handler pre-loads the lower-timeframe `TimeSeries<Int64Bar>` per asset and passes it to the engine as auxiliary data (separate from the strategy's subscription data). This auxiliary data is NOT delivered to the strategy via `OnBar`. When the strategy already subscribes to the asset's `SmallestInterval`, detailed execution mode has no additional effect
- **FR-014**: System MUST fall back to worst-case assumption when detailed execution mode is enabled but lower-timeframe data is unavailable for the ambiguous period. The engine SHOULD surface this condition via the `BacktestResult` (e.g., a warnings collection) rather than requiring a logging dependency in the Domain layer
- **FR-015**: System MUST process multiple pending orders for the same asset in submission order (FIFO) within a single bar tick
- **FR-016**: System MUST generate fill events for all order executions, including SL and TP fills, with price, quantity, timestamp, side, and commission. *(Note: SL vs TP distinction is inferred by the engine via `hitTpIndex`, not stored on the Fill.)*
- ~~**FR-017**: System MUST provide a portable order tracking module that maintains position state, SL levels, and multiple TP levels based solely on fill events~~ *(Deferred with US4)*
- ~~**FR-018**: System MUST ensure the order tracking module contains no backtest-specific or broker-specific code, operating identically across environments~~ *(Deferred with US4)*
- ~~**FR-019**: System MUST support multiple take-profit levels with configurable partial position closure percentages in the tracking module~~ *(Deferred with US4)*
- **FR-020**: System MUST allow strategies to cancel pending orders that have not yet been fully filled
- **FR-021**: System MUST update portfolio state (cash, positions, realized P&L) after each fill event
- **FR-022**: System MUST provide a history repository interface that wraps existing data loading capabilities and is usable by the backtest engine to load data for any configured subscription
- **FR-023**: System MUST reject an order and generate a rejection event when filling it would cause negative cash (insufficient funds); no implicit margin is provided
- **FR-024**: System MUST treat all pending orders as Good-Til-Cancelled (GTC) — orders persist in the queue until filled or explicitly cancelled by the strategy; no automatic expiry is provided

### Key Entities

- **History Repository**: Abstraction over data loading that resolves a subscription (asset + timeframe + date range) into a sequence of `Int64Bar` values. Wraps existing data sources and handles resampling.
- **Order Queue**: In-memory collection of pending orders submitted by the strategy. Processed by the engine each bar tick. Orders have a lifecycle: Pending → Triggered (for Stop/StopLimit) → Filled or Cancelled.
- **Order** (extended): The existing `Order` class extended with stop-loss price, take-profit levels, and stop trigger price in addition to the existing fields (side, type, quantity, limit price).
- **Fill**: Record of an order execution including order ID, fill price, quantity, side, timestamp, and commission.
- **~~Order Tracking Module~~** (`OrderTracker`): *Deferred from current scope for redesign.* Was intended as an optional strategy-side utility for position state tracking. Will be re-specified in a future iteration.
- **Backtest Configuration**: Extended configuration that includes all strategy subscriptions, date range, initial capital, commission, slippage, and the optional detailed execution logic flag.

### Assumptions

- The position model is netting: one net position per asset. A Buy fill reduces a short or increases a long. Simultaneous long and short positions on the same asset are not supported at the engine level; advanced hedging is the strategy's responsibility
- Strategies MAY maintain internal analytical state (e.g., indicator buffers, bar history windows) between bars for decision-making purposes. However, all execution state (order submission, position tracking, fill observation) MUST flow through the host-provided `IOrderContext`. This aligns with Constitution Principle I v1.3.0 which distinguishes analytical state from execution state
- The existing `IInt64BarLoader` (or its successor `IHistoryRepository`) provides the raw data; the engine handles resampling and chronological merging
- 1-minute resolution is the smallest stored interval; all higher timeframes are resampled from it
- The order tracking module operates purely on fill events and does not directly interact with the broker/exchange — that integration is deferred to a future feature
- Not all production brokers/exchanges support server-side SL/TP, which is why the tracking module exists on the strategy side
- The existing `StrategyAction` pattern is dead code and will be replaced by the order queue approach
- Lower-timeframe data for detailed execution logic means the asset's `SmallestInterval` (typically 1-minute bars). This data may not always be available, and the engine must handle this gracefully by falling back to worst-case

## Clarifications

### Session 2026-02-14

- Q: Should the strategy receive the full `TimeSeries<Int64Bar>` or just the new bar? → A: Only the new completed `Int64Bar`. The strategy operates under live-like conditions: it receives one bar at a time and must accumulate its own history if needed.
- Q: Should `IHistoryRepository` be part of this spec or deferred? → A: Include it. The engine needs it, and it wraps the existing `IInt64BarLoader` / data source interfaces.
- Q: Should the order tracking module be fully implemented or just the interface? → A: Define the portable interface and implement the backtest side only. The interface is designed for future broker adapter integration.
- Q: What happens when a bar's range covers both SL and TP for a pending order? → A: Worst-case assumption (SL hit first) by default. Optionally, enable detailed execution logic that examines lower-timeframe data to determine the actual sequence. If lower-timeframe data is unavailable, fall back to worst-case.
- Q: Risk: backtest engine awareness of SL/TP may not match production brokers. → A: Mitigated by the order tracking module, which tracks SL/TP on the strategy side independently of broker capabilities. The engine generates fill events; the module tracks state on its own. Same logic in backtest and production.
- Q: Position model — netting (single net position per asset) or hedging (simultaneous long/short)? → A: Netting. One net position per asset; a Buy reduces a short or increases a long. Advanced hedging logic is the strategy's responsibility.
- Q: What happens when a fill would cause negative cash (insufficient funds)? → A: Reject the order. The strategy receives a rejection event. No implicit margin or partial fills.
- Q: Do pending orders expire, or must strategies cancel them explicitly? → A: GTC (Good-Til-Cancelled) only. All pending orders persist until filled or explicitly cancelled by the strategy. Time-based expiry is the strategy's responsibility.
- Q: What is the performance target for bar processing throughput? → A: Baseline SLA of 1 minute per 500K bars. To be refined as optimal algorithms and data structures are identified.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A backtest with two subscriptions (different assets, same timeframe) over one month of 1-minute data completes without error, feeding bars in correct chronological order
- **SC-002**: A strategy using all four order types (Market, Limit, Stop, StopLimit) produces fill events with correct prices and timestamps for each order type
- **SC-003**: A backtest where a pending order's SL and TP are both within a single bar's range produces the worst-case outcome (SL hit) when detailed execution logic is disabled
- ~~**SC-004**: The order tracking module, given a deterministic sequence of fill events, produces identical position state and P&L whether run in a backtest or instantiated standalone with the same fill sequence~~ *(Deferred with US4)*
- **SC-005**: A strategy placing 100 orders in a single backtest run has all orders processed without queue corruption, lost orders, or duplicate fills
- **SC-006**: Resampling 1-minute bars into 5-minute bars produces bars with correct aggregated OHLCV values matching manual calculation
- **SC-007**: The backtest engine processes bars at a baseline rate of at least 500K bars per minute (e.g., three subscriptions over one year of 1-minute data — ~1.5M bars — completes within 3 minutes). This SLA is a starting baseline to be refined as optimal algorithms and data structures are identified
