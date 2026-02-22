# Phase 3 — Explicit Event Emission

**Parent:** `docs/debug-feature/requirements.md` §11.4
**Depends on:** Phase 2 (JSONL sink wired up)
**Unlocks:** Phase 4, Phase 5, Phase 6

---

## Acceptance Criteria

### BacktestEngine Emission

- [x] `IEventBus` injected into `BacktestEngine.Run()` (optional parameter, defaults to `NullEventBus`)
- [x] `run.start` event emitted after strategy `OnInit()` — includes config/params snapshot
- [x] `run.end` event emitted after loop exit — includes summary stats (net profit, trade count, etc.)
- [x] `bar` event emitted after each closed bar is fully processed (after OnBarComplete)
- [x] `bar.mut` event emitted when an open bar is mutated (if applicable to engine design)
- [x] Bar events carry: timestamp, OHLCV, subscription index, asset, timeframe
- [x] `err` event emitted on caught exceptions during run
- [x] `warn` event emitted on non-fatal anomalies (e.g. order rejection reasons)

### Order Lifecycle Emission

- [x] `ord.place` emitted when an order is submitted to the execution engine
- [x] `ord.fill` emitted when an order is filled (partial or full) — includes fill price, quantity, commission
- [x] `ord.cancel` emitted when an order is cancelled
- [x] `ord.reject` emitted when an order is rejected — includes rejection reason
- [x] `pos` emitted when a position changes (open, increase, decrease, close) — includes direction, quantity, average entry, realized PnL
- [x] All order/position events carry the order ID for lifecycle correlation

### Strategy Signal Emission

- [x] `IEventBus` injected into `StrategyBase` (or passed via `OnBar` context)
- [x] `sig` event emitted when strategy generates a trading signal
- [x] Signal events carry: signal direction, strength/confidence, triggering bar reference

### Risk Emission

- [x] `risk` event emitted when risk evaluator performs a check
- [x] Carries: pass/reject decision, reason string, order reference

### ExportMode Filtering Verification

- [x] Backtest mode: all event types emitted (bar, ind, sig, risk, ord.*, pos, run.*, err, warn)
- [x] Optimization mode: only `ord.*`, `pos`, `run.*`, `err`, `warn` emitted — bar/sig/risk/ind silently dropped
- [x] Live mode: `ord.*`, `pos`, `sig`, `risk`, `run.*`, `err`, `warn` emitted — bar/ind dropped

### Injection Pattern

- [x] Components receiving `IEventBus`: `BacktestEngine`, order processing, `StrategyBase`, `IRiskEvaluator`
- [x] Injection is via constructor or method parameter — not global/static
- [x] Existing callers not using debug/event export pass `NullEventBus` (zero overhead path)
- [x] Normal backtest and optimization paths have no performance regression

### Tests

- [x] Unit test: `BacktestEngine` emits `run.start` → N × `bar` → `run.end` in correct order
- [x] Unit test: fill events emitted with correct price/quantity from known test scenario
- [x] Unit test: `sig` event emitted when strategy signals (mock strategy)
- [x] Unit test: `risk` event emitted with pass/reject on risk check
- [x] Unit test: optimization mode — verify `bar`, `sig`, `risk`, `ind` events NOT emitted
- [x] Unit test: position events track full lifecycle (open → modify → close)
- [x] Integration test: full backtest with known data → collect all events → verify complete narrative (run.start, bars, signals, orders, fills, positions, run.end)
- [x] Integration test: JSONL file contains valid event stream matching engine execution
- [x] Performance test: existing 500K-bar benchmark does not regress when `NullEventBus` is used
- [x] All existing tests pass unchanged

### Non-Functional

- [x] `IEventBus` injection does not change existing public API signatures of `BacktestEngine.Run()` for non-debug callers (optional param with default)
- [x] No allocation on the `NullEventBus` path
- [x] Event emission is a single `bus.Emit(...)` call at each site — no conditional logic at call sites
