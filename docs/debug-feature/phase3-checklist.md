# Phase 3 — Explicit Event Emission

**Parent:** `docs/debug-feature/requirements.md` §11.4
**Depends on:** Phase 2 (JSONL sink wired up)
**Unlocks:** Phase 4, Phase 5, Phase 6

---

## Acceptance Criteria

### BacktestEngine Emission

- [ ] `IEventBus` injected into `BacktestEngine.Run()` (optional parameter, defaults to `NullEventBus`)
- [ ] `run.start` event emitted after strategy `OnInit()` — includes config/params snapshot
- [ ] `run.end` event emitted after loop exit — includes summary stats (net profit, trade count, etc.)
- [ ] `bar` event emitted after each closed bar is fully processed (after OnBarComplete)
- [ ] `bar.mut` event emitted when an open bar is mutated (if applicable to engine design)
- [ ] Bar events carry: timestamp, OHLCV, subscription index, asset, timeframe
- [ ] `err` event emitted on caught exceptions during run
- [ ] `warn` event emitted on non-fatal anomalies (e.g. order rejection reasons)

### Order Lifecycle Emission

- [ ] `ord.place` emitted when an order is submitted to the execution engine
- [ ] `ord.fill` emitted when an order is filled (partial or full) — includes fill price, quantity, commission
- [ ] `ord.cancel` emitted when an order is cancelled
- [ ] `ord.reject` emitted when an order is rejected — includes rejection reason
- [ ] `pos` emitted when a position changes (open, increase, decrease, close) — includes direction, quantity, average entry, realized PnL
- [ ] All order/position events carry the order ID for lifecycle correlation

### Strategy Signal Emission

- [ ] `IEventBus` injected into `StrategyBase` (or passed via `OnBar` context)
- [ ] `sig` event emitted when strategy generates a trading signal
- [ ] Signal events carry: signal direction, strength/confidence, triggering bar reference

### Risk Emission

- [ ] `risk` event emitted when risk evaluator performs a check
- [ ] Carries: pass/reject decision, reason string, order reference

### ExportMode Filtering Verification

- [ ] Backtest mode: all event types emitted (bar, ind, sig, risk, ord.*, pos, run.*, err, warn)
- [ ] Optimization mode: only `ord.*`, `pos`, `run.*`, `err`, `warn` emitted — bar/sig/risk/ind silently dropped
- [ ] Live mode: `ord.*`, `pos`, `sig`, `risk`, `run.*`, `err`, `warn` emitted — bar/ind dropped

### Injection Pattern

- [ ] Components receiving `IEventBus`: `BacktestEngine`, order processing, `StrategyBase`, `IRiskEvaluator`
- [ ] Injection is via constructor or method parameter — not global/static
- [ ] Existing callers not using debug/event export pass `NullEventBus` (zero overhead path)
- [ ] Normal backtest and optimization paths have no performance regression

### Tests

- [ ] Unit test: `BacktestEngine` emits `run.start` → N × `bar` → `run.end` in correct order
- [ ] Unit test: fill events emitted with correct price/quantity from known test scenario
- [ ] Unit test: `sig` event emitted when strategy signals (mock strategy)
- [ ] Unit test: `risk` event emitted with pass/reject on risk check
- [ ] Unit test: optimization mode — verify `bar`, `sig`, `risk`, `ind` events NOT emitted
- [ ] Unit test: position events track full lifecycle (open → modify → close)
- [ ] Integration test: full backtest with known data → collect all events → verify complete narrative (run.start, bars, signals, orders, fills, positions, run.end)
- [ ] Integration test: JSONL file contains valid event stream matching engine execution
- [ ] Performance test: existing 500K-bar benchmark does not regress when `NullEventBus` is used
- [ ] All existing tests pass unchanged

### Non-Functional

- [ ] `IEventBus` injection does not change existing public API signatures of `BacktestEngine.Run()` for non-debug callers (optional param with default)
- [ ] No allocation on the `NullEventBus` path
- [ ] Event emission is a single `bus.Emit(...)` call at each site — no conditional logic at call sites
