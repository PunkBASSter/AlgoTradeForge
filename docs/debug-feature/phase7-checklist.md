# Phase 7 — Per-Event Stepping

**Parent:** `docs/debug-feature/requirements.md` §11.8
**Depends on:** Phase 1 (event bus), Phase 0 (debug probe), at least one emission phase (3 or 4)
**Unlocks:** Fine-grained interactive debugging of signals, indicators, and order lifecycle

---

## Acceptance Criteria

### IDebugProbe Extension

- [ ] `IDebugProbe` extended with `OnEventEmitted(string eventType, DebugSnapshot snapshot)` method
- [ ] `NullDebugProbe` implements new method as no-op
- [ ] `GatingDebugProbe` evaluates break conditions against event type in `OnEventEmitted()`
- [ ] Bar-boundary gate (`OnBarProcessed`) remains — now a special case of per-event gating
- [ ] `IEventBus` calls `probe.OnEventEmitted()` after each emitted event (post-filter)

### New Commands

- [ ] `next_signal` — advance until next `sig` event, then pause
- [ ] `next_type { "_t": "bar" }` — advance until next event of the specified type, then pause
- [ ] `set_export { "mutations": true }` — toggle opt-in event categories (`bar.mut`, `ind.mut`) mid-run

### New Break Conditions

- [ ] `OnSignal` — break when event type is `sig`
- [ ] `OnEventType(string type)` — break when event type matches specified string
- [ ] Compound AND condition — compose two `BreakCondition` instances (e.g. "next trade after timestamp X")
- [ ] Compound conditions evaluated correctly: both sub-conditions must be satisfied

### set_export Runtime Toggle

- [ ] `set_export` command modifies the `ExportMode` filter on the running `EventBus`
- [ ] Toggling `mutations: true` enables `bar.mut` and `ind.mut` events mid-run
- [ ] Toggling `mutations: false` disables them again
- [ ] Change takes effect immediately — next event is filtered against updated config
- [ ] Thread-safe: toggle from control thread, read from engine thread

### Stepping Semantics

- [ ] All stepping commands operate on **exported** events only (post-filter)
- [ ] `next_signal` skips bars/indicators/orders and pauses only on `sig`
- [ ] `next_type { "_t": "ord.fill" }` pauses on fills only
- [ ] `next_bar` still works as before (bar-boundary stepping unchanged)
- [ ] `next_trade` still works as before (fill-bar stepping unchanged)

### Integration with Event Bus

- [ ] `EventBus` calls `probe.OnEventEmitted()` for each event that passes the export filter
- [ ] Probe can block execution at any event boundary (not just bar boundaries)
- [ ] Sequence of callbacks: `...events emitted during bar processing... → OnBarProcessed()`
- [ ] Fine-grained stepping within a single bar's events is possible (e.g. stop after signal but before order fill)

### Tests

- [ ] Unit test: `next_signal` break condition fires on `sig` event, ignores `bar`/`ind`/`ord` events
- [ ] Unit test: `next_type("ord.fill")` fires on fill event, ignores all others
- [ ] Unit test: compound condition `OnFillBar AND AtTimestamp(X)` — only fires when both satisfied
- [ ] Unit test: `set_export` toggles mutation events on/off mid-run
- [ ] Unit test: `OnEventEmitted` called for each exported event in correct order
- [ ] Integration test: step through a bar that produces signal → order → fill using `next_signal` then `next_type("ord.fill")` — verify intermediate states
- [ ] Integration test: `set_export { mutations: true }` mid-run → verify `bar.mut` events now appear
- [ ] Backward compat: all Phase 0 commands (`next_bar`, `next`, `next_trade`, `continue`, `pause`, `run_to_*`) still work identically
- [ ] All existing tests pass unchanged

### WebSocket Transport Integration

- [ ] Phase 7 commands (`next_signal`, `next_type`, `set_export`) are accessible via WebSocket transport (Phase 6)
- [ ] WebSocket command parser handles new command types and payloads
- [ ] Responses for new commands follow the same `DebugSnapshot` format as existing commands

### Non-Functional

- [ ] Per-event probe call adds negligible overhead (single virtual dispatch + condition check)
- [ ] `set_export` toggle is lock-free (`Volatile.Write` for flag, matching Phase 0 thread model)
- [ ] No breaking changes to `IDebugProbe` for existing callers (default interface method or backward-compat pattern)
