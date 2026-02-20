# Phase 4 — Indicator Factory & Automatic Emission

**Parent:** `docs/debug-feature/requirements.md` §11.5
**Depends on:** Phase 3 (explicit emission wired up)
**Unlocks:** Phase 7 (ind events available for per-event stepping)

---

## Acceptance Criteria

### IIndicatorFactory Interface

- [x] `IIndicatorFactory` interface defined in Application layer
- [x] Single method: `IIndicator<TInp, TBuff> Create<TInp, TBuff>(IIndicator<TInp, TBuff> indicator, DataSubscription subscription)`
- [x] Strategies receive factory via constructor injection

### EmittingIndicator Decorator

- [x] `EmittingIndicator<TInp, TBuff>` implements `IIndicator<TInp, TBuff>`
- [x] Delegates all properties to inner indicator: `Name`, `Measure`, `Buffers`, `MinimumHistory`, `CapacityLimit`
- [x] `Compute()` calls `inner.Compute(series)` then emits `IndEvent` with latest buffer values
- [x] Strongly typed — no reflection, no boxing of `Int64Bar` (record struct), no allocation beyond the event record
- [x] `ind` event includes: indicator name, measure, buffer snapshot, associated `DataSubscription`
- [x] Decorator fires `ind` event if and only if `Compute()` is called — correctness for multi-subscription strategies where not all indicators compute on every bar

### Factory Implementations

- [x] `EmittingIndicatorFactory` — wraps indicator with `EmittingIndicator` decorator (backtest/debug mode)
- [x] `PassthroughIndicatorFactory` — returns raw indicator unwrapped (optimization mode)
- [x] Factory selected at run setup time based on execution mode — one-time decision, no per-call branch
- [x] `PassthroughIndicatorFactory` has zero overhead: no allocation, no wrapper, no flag check per bar

### Strategy Integration

- [x] `IIndicatorFactory` injected into strategy constructors (or available through init context)
- [x] Existing strategies updated to call `indicators.Create(new ConcreteIndicator(...), subscription)`
- [x] Strategy holds `IIndicator<TInp, TBuff>` interface reference — all existing indicator access patterns (`Buffers`, `Name`, `Compute`) work through the interface
- [x] No changes to indicator source code — indicators remain pure Domain types with zero event system dependencies

### Mutation Events (ind.mut)

- [x] `ind.mut` events emitted when `Compute()` is called on a bar mutation (bar.mut scenario)
- [x] `ind.mut` events are opt-in (`ExportMode` Backtest only) — silently dropped in optimization/live modes

### Multi-Subscription Correctness

- [x] In multi-TF strategy (e.g. M1 + H1), H1 indicator emits `ind` only on H1 bars — not on M1 bars
- [x] Indicator decorator does not emit stale snapshots — emission tied to actual `Compute()` invocation

### Optimization Module Composition (§10.9)

- [x] Indicators created via optimization module injection (`registry.Create(...)`) flow through the same `IIndicatorFactory.Create()` path
- [x] Factory is the universal decoration point regardless of how the indicator was constructed

### Tests

- [x] Unit test: `EmittingIndicatorFactory.Create()` returns a decorated indicator
- [x] Unit test: `PassthroughIndicatorFactory.Create()` returns the same instance (reference equality)
- [x] Unit test: decorated indicator emits `IndEvent` after `Compute()` with correct name/values
- [x] Unit test: decorated indicator delegates all properties to inner indicator
- [x] Unit test: no `ind` event emitted if `Compute()` is not called (multi-TF correctness)
- [x] Unit test: `ind.mut` emitted on mutation-triggered compute
- [x] Integration test: full backtest → verify `ind` events appear in JSONL for each indicator compute
- [x] Integration test: optimization run → verify zero `ind` events (passthrough factory used)
- [x] All existing tests pass unchanged
- [x] Existing indicator unit tests remain unchanged (indicators have no factory dependency)

### Non-Functional

- [x] No reflection or `DispatchProxy` — hand-written generic decorator only
- [x] Performance: decorated path adds only the event record allocation per `Compute()` call
- [x] No new NuGet packages
