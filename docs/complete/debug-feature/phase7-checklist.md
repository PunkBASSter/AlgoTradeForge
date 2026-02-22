# Phase 7 — Per-Event Stepping

**Parent:** `docs/debug-feature/requirements.md` §11.8
**Depends on:** Phase 1 (event bus), Phase 0 (debug probe), at least one emission phase (3 or 4)
**Unlocks:** Fine-grained interactive debugging of signals, indicators, and order lifecycle

---

## Acceptance Criteria

### IDebugProbe Extension

- [x] `IDebugProbe` extended with `OnEventEmitted(string eventType, DebugSnapshot snapshot)` method
- [x] `NullDebugProbe` implements new method as no-op
- [x] `GatingDebugProbe` evaluates break conditions against event type in `OnEventEmitted()`
- [x] Bar-boundary gate (`OnBarProcessed`) remains — now a special case of per-event gating
- [x] `IEventBus` calls `probe.OnEventEmitted()` after each emitted event (post-filter)

### New Commands

- [x] `next_signal` — advance until next `sig` event, then pause
- [x] `next_type { "_t": "bar" }` — advance until next event of the specified type, then pause
- [x] `set_export { "mutations": true }` — toggle opt-in event categories (`bar.mut`, `ind.mut`) mid-run

### New Break Conditions

- [x] `OnSignal` — break when event type is `sig`
- [x] `OnEventType(string type)` — break when event type matches specified string
- [x] Compound AND condition — compose two `BreakCondition` instances (e.g. "next trade after timestamp X")
- [x] Compound conditions evaluated correctly: both sub-conditions must be satisfied
  - Cross-granularity AND (mixing event-level + bar-level) throws `ArgumentException` at construction time.

### set_export Runtime Toggle

- [x] `set_export` command modifies the `ExportMode` filter on the running `EventBus`
- [x] Toggling `mutations: true` enables `bar.mut` and `ind.mut` events mid-run
- [x] Toggling `mutations: false` disables them again
- [x] Change takes effect immediately — next event is filtered against updated config
- [x] Thread-safe: toggle from control thread, read from engine thread

### Stepping Semantics

- [x] All stepping commands operate on **exported** events only (post-filter)
- [x] `next_signal` skips bars/indicators/orders and pauses only on `sig`
- [x] `next_type { "_t": "ord.fill" }` pauses on fills only
- [x] `next_bar` still works as before (bar-boundary stepping unchanged)
- [x] `next_trade` still works as before (fill-bar stepping unchanged)

### Integration with Event Bus

- [x] `EventBus` calls `probe.OnEventEmitted()` for each event that passes the export filter
- [x] Probe can block execution at any event boundary (not just bar boundaries)
- [x] Sequence of callbacks: `...events emitted during bar processing... → OnBarProcessed()`
- [x] Fine-grained stepping within a single bar's events is possible (e.g. stop after signal but before order fill)

### Tests

- [x] Unit test: `next_signal` break condition fires on `sig` event, ignores `bar`/`ind`/`ord` events
- [x] Unit test: `next_type("ord.fill")` fires on fill event, ignores all others
- [x] Unit test: compound condition — same-granularity AND works, cross-granularity AND throws `ArgumentException`
- [x] Unit test: `set_export` toggles mutation events on/off mid-run
- [x] Unit test: `OnEventEmitted` called for each exported event in correct order
- [ ] Integration test: step through a bar that produces signal → order → fill using `next_signal` then `next_type("ord.fill")` — verify intermediate states
- [ ] Integration test: `set_export { mutations: true }` mid-run → verify `bar.mut` events now appear
- [x] Backward compat: all Phase 0 commands (`next_bar`, `next`, `next_trade`, `continue`, `pause`, `run_to_*`) still work identically
- [x] All existing tests pass unchanged

### WebSocket Transport Integration

- [x] Phase 7 commands (`next_signal`, `next_type`, `set_export`) are accessible via WebSocket transport (Phase 6)
- [x] WebSocket command parser handles new command types and payloads
- [x] Responses for new commands follow the same `DebugSnapshot` format as existing commands

### Non-Functional

- [x] Per-event probe call adds negligible overhead (single virtual dispatch + condition check)
- [x] `set_export` toggle is lock-free (`Volatile.Write` for flag, matching Phase 0 thread model)
- [x] No breaking changes to `IDebugProbe` for existing callers (default interface method or backward-compat pattern)
