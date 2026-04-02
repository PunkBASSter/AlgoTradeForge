# Feature Specification: Strategy Module Framework

**Feature Branch**: `027-strategy-module-framework`
**Created**: 2026-04-02
**Status**: Draft
**Input**: User description: "Create a framework for making modular strategies out of pre-made and tested building blocks supporting different workflows and strategy types, compatible with the existing backtest, optimization, validation engines and live connectors (Binance), integrated with the strategy-sided trade module."

## Clarifications

### Session 2026-04-02

- Q: Is ATR/volatility automatically computed by the base class or must each strategy create its own ATR indicator? → A: Each strategy creates its own ATR indicator and writes to context manually. No hidden computation cost; strategies that don't need ATR (structure-based or z-score stops) pay nothing.
- Q: Does the trailing stop module maintain state per order group or per strategy? → A: Single module instance tracks stop state per order group internally (keyed by group ID). One instance per strategy; per-group stop levels are isolated and ratchet independently.
- Q: For multi-subscription strategies, does the entry pipeline run on every bar or only the primary subscription? → A: Phase 1 (update context) runs for all subscriptions; Phases 2 and 3 (manage positions, evaluate entry) run only on the primary subscription (first in the subscriptions list). Secondary bars update cross-asset modules and context but do not trigger trade decisions.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Build a Minimal Strategy with One Override (Priority: P1)

A strategy developer wants to create a new mean-reversion strategy by inheriting from a modular base class and implementing only the signal generation logic. All other pipeline steps (position management, exit handling, sizing, order submission) are handled by the base class using sensible defaults. The developer registers indicator-based filters via pre-built filter modules and relies on the default ATR-based risk levels and market order entry.

**Why this priority**: This is the foundational use case. If the base class pipeline, context object, and module registration work for a single-override strategy, the framework's core orchestration is proven. Every subsequent story builds on this.

**Independent Test**: Can be fully tested by creating a minimal strategy that overrides only the signal generation method, running a backtest against historical data, and verifying that entries, exits (via default ATR stop), and position sizing all execute correctly without any additional overrides.

**Acceptance Scenarios**:

1. **Given** a strategy that overrides only the signal generation method and registers one filter module, **When** a backtest is run with historical bar data, **Then** the three-phase pipeline (update context, manage positions, evaluate entry) executes in the correct order on every bar, entries are placed when the signal exceeds the configured threshold, and default ATR-based stop-losses are applied.
2. **Given** a strategy using default risk levels and default market-order entry, **When** the position sizing module calculates quantity, **Then** it uses the configured risk percentage, entry price, and stop-loss distance, respects asset minimum/maximum order constraints, and rounds quantity appropriately.
3. **Given** an active position managed by the default exit mechanism, **When** the trailing stop is ratcheted to a new level, **Then** the stop-loss order on the trade group is updated, and the stop only moves in the favorable direction (never widens risk).
4. **Given** the strategy emits signals and the filter gate blocks a trade, **When** the composite filter score is below the configured threshold, **Then** no entry order is submitted and the pipeline short-circuits cleanly without errors.

---

### User Story 2 - Build a Customized Trend-Following Strategy (Priority: P2)

A strategy developer wants to build a Donchian-channel breakout strategy that customizes entry price (stop orders at channel boundaries), risk levels (structure-based stops), and uses a regime detector to avoid trading in range-bound markets. The developer registers a trailing stop module and a regime filter, relying on the framework to orchestrate everything.

**Why this priority**: Demonstrates that the virtual override points (entry price, risk levels) work correctly and that multiple modules (regime detector, trailing stop, filters) compose without conflicts. This validates the framework handles non-trivial strategy workflows.

**Independent Test**: Can be fully tested by creating a breakout strategy with custom entry price and risk level overrides, running a backtest, and verifying that stop orders are placed at channel levels, trailing stops ratchet correctly, and the regime filter prevents entries during unfavorable market conditions.

**Acceptance Scenarios**:

1. **Given** a strategy overriding entry price to return a stop order at the Donchian channel boundary, **When** a breakout signal fires, **Then** the submitted order uses the stop order type at the specified channel level (not a market order).
2. **Given** a regime detector module is registered and the market is in a range-bound regime, **When** the filter gate evaluates, **Then** the regime filter returns a score that blocks entry, and the pipeline short-circuits before signal generation.
3. **Given** a strategy with a trailing stop module and custom ATR-based risk levels, **When** a position is open and price moves favorably, **Then** the trailing stop ratchets up (for longs) or down (for shorts), and the trade group's stop-loss order is updated accordingly.
4. **Given** exit rules include regime-change detection, **When** the regime changes from trending to range-bound, **Then** the exit module produces a score that triggers position closure.

---

### User Story 3 - Build an Advanced Multi-Leg Strategy (Priority: P3)

A strategy developer wants to build a pairs-trading strategy that operates on two assets simultaneously, submits correlated entry orders for both legs, applies cointegration-based exit rules, and uses z-score-based signal generation. This requires overriding signal generation, risk levels, order execution (two legs), and custom exit evaluation.

**Why this priority**: Validates the framework's flexibility for complex, multi-asset strategies that override all virtual methods and use cross-asset modules. This is the most demanding use case and proves the framework doesn't constrain advanced workflows.

**Independent Test**: Can be fully tested by creating a pairs strategy with two data subscriptions, running a backtest, and verifying that both legs are entered/exited simultaneously, z-score signals fire at configured thresholds, and cointegration breakdown triggers emergency exit.

**Acceptance Scenarios**:

1. **Given** a pairs strategy subscribed to two assets and using a cross-asset module, **When** the z-score crosses the entry threshold, **Then** two correlated orders are submitted (one buy, one sell) with quantities adjusted by the hedge ratio.
2. **Given** an active pairs position, **When** cointegration breaks down (statistical test indicates the spread relationship no longer holds), **Then** the custom exit evaluation returns the maximum exit score, and both legs are closed immediately.
3. **Given** a pairs strategy overriding order execution, **When** entry is triggered, **Then** the custom `OnExecuteEntry` submits linked orders for both assets rather than a single order for one asset.
4. **Given** the z-score reverts past the exit threshold, **When** the custom exit evaluation runs, **Then** both legs are closed at market price.

---

### User Story 4 - Optimize a Modular Strategy's Parameters (Priority: P2)

A strategy developer wants to run a parameter optimization on a modular strategy, sweeping over both top-level pipeline parameters (signal threshold, filter threshold, ATR stop multiplier) and nested module parameters (money management risk percentage, trailing stop activation distance, exit rule thresholds).

**Why this priority**: Optimization compatibility is critical for practical use. If nested module parameters are not discoverable by the optimization engine, the framework's value is severely limited.

**Independent Test**: Can be fully tested by configuring an optimization run for a modular strategy with nested module parameters, running it, and verifying that the optimizer discovers all annotated parameters (including nested ones), generates the correct parameter combinations, and each trial runs independently.

**Acceptance Scenarios**:

1. **Given** a modular strategy with annotated parameters at the top level and within nested module parameter objects, **When** the optimization engine evaluates the parameter space, **Then** it discovers all annotated parameters including those nested in module param objects (money management, trailing stop, exit rules).
2. **Given** an optimization run with parallel trials, **When** each trial creates a fresh strategy instance, **Then** no module state is shared between trials (each trial has its own module instances, indicators, and context).
3. **Given** a strategy with filter weight parameters in a dictionary keyed by module identifier, **When** the optimizer sweeps filter weights, **Then** the correct weights are applied to the corresponding filter modules during each trial.

---

### User Story 5 - Run a Modular Strategy on Live Binance Connector (Priority: P3)

A strategy developer wants to deploy a modular strategy to the live Binance connector. The strategy's trade registry module must reconcile expected orders with actual exchange state, and the pipeline must handle real-time bar delivery identically to backtest bar delivery.

**Why this priority**: Live compatibility is essential but depends on the framework working correctly in backtest first. The key concern is that the sealed pipeline, module lifecycle, and trade registry reconciliation work identically in both environments.

**Independent Test**: Can be fully tested by deploying a modular strategy to the live connector in paper-trading mode, delivering bars, and verifying that orders are submitted to the exchange, fills are routed back to the trade registry, and stop-loss updates propagate to the exchange.

**Acceptance Scenarios**:

1. **Given** a modular strategy deployed on the live Binance connector, **When** a bar completes, **Then** the same three-phase pipeline runs as in backtest (update context, manage positions, evaluate entry).
2. **Given** the trade registry has an active order group, **When** a fill is received from the exchange, **Then** the trade registry maps the fill to the correct order group and updates its state.
3. **Given** the trailing stop module ratchets to a new level, **When** the stop-loss update is issued, **Then** the trade registry submits a stop-loss modification to the exchange via the order context.
4. **Given** the live connector detects a mismatch between expected and actual orders (e.g., after a disconnect), **When** reconciliation runs, **Then** the trade registry's expected-order list is compared against exchange state and missing orders are repaired.

---

### User Story 6 - Compose Exit Rules from Pre-Built Building Blocks (Priority: P2)

A strategy developer wants to configure position exit behavior by composing multiple pre-built exit rules (time-based, profit target, trailing stop hit, regime change, signal reversal) without writing custom exit logic. The most aggressive exit rule wins (most negative score triggers the exit).

**Why this priority**: Exit rule composition is a core value proposition of the framework — it allows developers to mix and match proven exit behaviors. This must work before developers will trust the framework for production strategies.

**Independent Test**: Can be fully tested by configuring an exit module with multiple rules (e.g., time-based + profit target), running a backtest, and verifying that positions are closed when any rule's score crosses the exit threshold, and that the most extreme score determines the outcome.

**Acceptance Scenarios**:

1. **Given** an exit module configured with a time-based rule (max 20 bars) and a profit-target rule (3x ATR), **When** the position has been held for 21 bars but profit target is not reached, **Then** the time-based rule returns the maximum exit score, and the position is closed.
2. **Given** multiple exit rules with different scores, **When** the exit module aggregates them, **Then** the most negative (most extreme) score is used as the composite exit signal.
3. **Given** a strategy with a custom exit evaluation method that returns a moderate exit score, **When** the exit module also returns a score, **Then** the more negative of the two scores is used (composing built-in and custom exit logic).

---

### User Story 7 - Use Position Sizing Strategies Interchangeably (Priority: P2)

A strategy developer wants to select a position-sizing method (fixed-fractional, ATR volatility targeting, or half-Kelly) via configuration, without changing strategy code. The money management module calculates the appropriate position size based on the selected method, the current risk parameters, and asset constraints.

**Why this priority**: Position sizing is universally needed and must be interchangeable without code changes. This validates the module configuration and dispatch pattern.

**Independent Test**: Can be fully tested by running the same strategy with different money management configurations and verifying that position sizes change according to the selected method while respecting asset minimum/maximum quantity constraints.

**Acceptance Scenarios**:

1. **Given** a fixed-fractional sizing method configured with 2% risk, **When** entry price is 100 and stop-loss is 98 (2-unit distance), **Then** quantity is calculated as `(equity * 0.02) / 2`, rounded down to the asset's lot step, and clamped to min/max order size.
2. **Given** an ATR volatility target method, **When** the current ATR changes, **Then** the position size adjusts inversely to volatility (higher ATR = smaller position).
3. **Given** the calculated position size is below the asset's minimum order quantity, **When** the sizing module returns, **Then** the pipeline stops and no entry order is submitted.

---

### Edge Cases

- What happens when all registered filters return neutral scores (0)? The composite filter score should default to "allowed" (pass-through).
- What happens when a strategy has no exit module and no trailing stop? Positions remain open until the strategy generates an opposing signal or is stopped externally.
- What happens when the trailing stop module has not been activated (no position yet)? It must be a no-op — no crash, no stale state from a previous position.
- What happens when two exit rules disagree (one says hold strongly, the other says close)? The most extreme negative score wins, ensuring safety takes priority.
- What happens when a module parameter object is null (e.g., no trailing stop configured)? The pipeline skips that module entirely with zero overhead.
- What happens when a strategy subscribes to multiple data feeds but the cross-asset module is not registered? Other modules process only the primary subscription without errors.
- What happens when a secondary subscription's bar arrives? Phase 1 runs (updating cross-asset modules and context), but Phases 2-3 are skipped — no position management or entry evaluation fires on secondary bars.
- What happens when the money management module calculates a quantity of zero? The pipeline stops gracefully — no division-by-zero, no zero-quantity orders.
- What happens when a fill arrives for an order not tracked by the trade registry (e.g., a manually placed order)? The trade registry ignores it without error.
- What happens when the regime detector encounters insufficient data during warmup? It returns `Unknown` regime, and any regime-dependent filter treats `Unknown` as neutral.

## Requirements *(mandatory)*

### Functional Requirements

**Pipeline & Base Class**

- **FR-001**: The system MUST provide a modular strategy base class that orchestrates a three-phase bar-processing pipeline: (1) update context, (2) manage existing positions, (3) evaluate new entry.
- **FR-002**: The pipeline MUST be sealed (not overridable) to guarantee consistent execution order across all strategies.
- **FR-003**: A concrete strategy MUST be required to implement only one method — signal generation — to produce a functional trading strategy; all other pipeline steps MUST have working defaults.
- **FR-004**: The base class MUST provide virtual override points for: entry price determination, risk level calculation, order execution, custom exit evaluation, and exit price determination.
- **FR-005**: The pipeline MUST short-circuit at each gate (capacity check, filter gate, signal threshold, stop-loss validation, minimum quantity) — if any check fails, subsequent steps MUST NOT execute.
- **FR-005a**: For strategies with multiple data subscriptions, Phase 1 (update context) MUST execute for bars from all subscriptions to keep cross-asset modules current. Phases 2 and 3 (manage positions, evaluate entry) MUST execute only when the primary subscription's bar arrives (first subscription in the list). Bars from secondary subscriptions MUST NOT trigger trade decisions.

**Module System**

- **FR-006**: The system MUST support registration of zero or more filter modules, each producing a directional score in the range [-100, +100].
- **FR-007**: Filter modules MUST be composable via weighted averaging, with weights configurable per module identifier.
- **FR-008**: The system MUST provide an exit module that aggregates multiple exit rules, where the most extreme negative score determines the composite exit signal.
- **FR-009**: Pre-built exit rules MUST include: time-based (max hold bars), profit target (ATR multiple), signal reversal, and regime change. Additional rules (session close, cointegration break) MUST be available for specialized strategy types.
- **FR-010**: The system MUST provide a trailing stop module that ratchets the stop level only in the favorable direction, supports multiple variants (ATR-based, Chandelier, Donchian), and tracks stop state independently per order group (keyed by group ID) within a single module instance.
- **FR-011**: The system MUST provide a money management module supporting at least three sizing methods: fixed-fractional risk, ATR volatility targeting, and half-Kelly.
- **FR-012**: The system MUST provide a regime detection module that classifies the current market state (e.g., trending, range-bound, volatile) and makes the classification available to filters and exit rules via the shared context.
- **FR-013**: Unregistered (null) modules MUST be skipped with zero overhead — no allocations, no virtual calls, no branching beyond a null check.

**Context & State Sharing**

- **FR-014**: The system MUST provide a shared context object updated each bar with: current bar data, equity, and cash (populated by the base class). ATR, volatility estimate, and market regime are strategy-populated — each strategy creates its own indicators and writes derived values to the context via the key-value store or dedicated context properties. The default risk-level implementation reads ATR from the context; strategies using default risk levels MUST populate ATR during initialization.
- **FR-015**: Modules MUST be able to publish and consume arbitrary typed data via the context using string keys, enabling loose coupling between modules (e.g., a cross-asset module publishes z-score; the signal generator reads it).

**Integration with Trade Registry**

- **FR-016**: The system MUST integrate with the existing trade registry module for order group lifecycle management (open, close, update stop-loss, cancel).
- **FR-017**: The trade registry MUST enforce a configurable maximum number of concurrent order groups, checked before entry evaluation begins.
- **FR-018**: Fill events MUST be routed through the trade registry to update order group state before the strategy's fill handler is invoked.

**Compatibility**

- **FR-019**: Modular strategies MUST implement the existing strategy interface so the backtest engine, optimization engine, and validation pipeline can use them without modification.
- **FR-020**: Module parameter objects MUST be discoverable by the existing optimization parameter resolver, including parameters nested inside module configuration objects.
- **FR-021**: Module parameter objects with monetary values MUST be automatically scaled by the existing parameter scaling system (quote-asset parameters declared in human-readable units, scaled to ticks at runtime).
- **FR-022**: Each backtest or optimization trial MUST create completely independent strategy and module instances with no shared mutable state, ensuring thread safety for parallel execution.
- **FR-023**: Modular strategies MUST be compatible with the live Binance connector, including trade registry reconciliation of expected vs. actual exchange orders.

**Events & Observability**

- **FR-024**: The pipeline MUST emit structured events at key decision points: signal generation, filter evaluation, exit evaluation, stop-loss updates, order group state changes, and risk checks.
- **FR-025**: Module state (regime, trailing stop level, filter scores, signal strength) MUST be observable through the existing debug probe mechanism.

**Performance**

- **FR-026**: All price-related computations within modules MUST use scaled integer (long) arithmetic, consistent with the existing domain convention.
- **FR-027**: Indicator values that do not require monetary precision MUST use double-precision floating point for computation efficiency.
- **FR-028**: Monetary values (equity, cash, PnL) and volume/lot quantities MUST use decimal where required for precision, and long (scaled ticks) within the domain layer.
- **FR-029**: The Phase 2 (manage positions) loop MUST be skipped entirely when the strategy has no open positions (zero iteration overhead).

**Model Strategies**

- **FR-030**: The system MUST include a working RSI(2) mean-reversion model strategy that demonstrates the minimal single-override pattern.
- **FR-031**: The system MUST include a working Donchian breakout model strategy that demonstrates custom entry prices, custom risk levels, trailing stop, and regime filtering.
- **FR-032**: The system MUST include a working pairs-trading model strategy that demonstrates multi-asset subscriptions, cross-asset modules, dual-leg order execution, and cointegration-break exit.

### Key Entities

- **ModularStrategyBase**: Abstract base class that extends the existing strategy base, adding module orchestration and the sealed three-phase pipeline. Concrete strategies inherit from this instead of the raw strategy base.
- **ModularStrategyParamsBase**: Parameter base class extending the existing strategy params, adding pipeline thresholds (filter, signal, exit), default risk parameters, nested module parameter objects, and filter weight configuration.
- **StrategyContext**: Per-bar state container holding current bar, equity, cash, regime, ATR, volatility, and a loosely-coupled key-value store for inter-module data sharing.
- **IFilterModule**: Module interface for entry filters that produce a directional score [-100, +100]. Composed via weighted averaging.
- **ExitModule**: Aggregator for exit rules. Holds a list of IExitRule instances and returns the most extreme (most negative) score.
- **IExitRule**: Individual exit condition that evaluates per bar per active position and returns a score [-100, +100].
- **TrailingStopModule**: Module that tracks and ratchets stop-loss levels in the favorable direction, supporting multiple trailing variants. Maintains independent state per order group (keyed by group ID) within a single instance.
- **MoneyManagementModule**: Module that calculates position size from entry price, stop-loss, equity, and the selected sizing method.
- **RegimeDetectorModule**: Module that classifies market state and publishes the regime to the shared context.
- **CrossAssetModule**: Module for multi-asset strategies that computes inter-asset statistics (z-score, hedge ratio, cointegration status).
- **OrderGroup**: Existing entity representing a group of related orders (entry, stop-loss, take-profits) managed as a unit by the trade registry.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can create a functional single-asset strategy by implementing one abstract method (signal generation), with all other behavior provided by defaults — verified by the RSI(2) model strategy producing correct backtest results.
- **SC-002**: A developer can create a multi-override strategy (custom entry price, custom risk levels, trailing stop, regime filter) by overriding virtual methods and registering modules — verified by the Donchian breakout model strategy producing correct backtest results.
- **SC-003**: A developer can create an advanced multi-asset strategy with custom order execution and exit logic — verified by the pairs-trading model strategy correctly entering and exiting both legs.
- **SC-004**: All three model strategies pass optimization runs where the optimizer discovers and sweeps all annotated parameters, including nested module parameters.
- **SC-005**: All three model strategies produce identical results when run through the backtest engine as they would if implemented using the raw strategy base (no behavioral regression from the framework abstraction).
- **SC-006**: The framework introduces no measurable performance regression: a modular strategy with one filter, one exit rule, and a trailing stop processes each bar within 10% of the equivalent hand-coded strategy's bar processing time.
- **SC-007**: All pre-built modules (filters, exit rules, trailing stop variants, sizing methods) have independent unit tests with 100% branch coverage of their core logic.
- **SC-008**: A modular strategy deployed to the live Binance connector in paper-trading mode completes a full order lifecycle (entry, stop-loss update, exit) with correct trade registry reconciliation.
