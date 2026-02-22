# Phase 1 — Event Model & Bus Core

**Parent:** `docs/debug-feature/requirements.md` §11.2
**Depends on:** Phase 0 (done)
**Unlocks:** Phase 2, Phase 7

---

## Acceptance Criteria

### Event Type Records

- [x] All event types from §2.2 defined as C# records implementing `IBacktestEvent`:
  - [x] `BarEvent` — bar closed (`bar`)
  - [x] `BarMutEvent` — open bar mutated (`bar.mut`)
  - [x] `IndEvent` — indicator computed for closed bar (`ind`)
  - [x] `IndMutEvent` — indicator recomputed on mutation (`ind.mut`)
  - [x] `SigEvent` — strategy signal generated (`sig`)
  - [x] `RiskEvent` — risk check performed (`risk`)
  - [x] `OrdPlaceEvent` — order submitted (`ord.place`)
  - [x] `OrdFillEvent` — order filled (`ord.fill`)
  - [x] `OrdCancelEvent` — order cancelled (`ord.cancel`)
  - [x] `OrdRejectEvent` — order rejected (`ord.reject`)
  - [x] `PosEvent` — position changed (`pos`)
  - [x] `RunStartEvent` — run began with config snapshot (`run.start`)
  - [x] `RunEndEvent` — run completed with summary (`run.end`)
  - [x] `ErrEvent` — error (`err`)
  - [x] `WarnEvent` — warning (`warn`)
  - [x] `TickEvent` — new tick received (`tick`) — opt-in, `ExportMode = Backtest`
- [x] Each record carries the canonical envelope fields: `ts` (ISO 8601 UTC), `sq` (uint64 monotonic), `_t` (event type string), `src` (emitting component), `d` (typed payload)
- [x] Event types live in Domain layer (no infrastructure dependencies)

### ExportMode

- [x] `[Flags] enum ExportMode { Backtest = 1, Optimization = 2, Live = 4 }` defined
- [x] Each event type annotated with its default `ExportMode` per §2.3 table:
  - `ord.*`, `pos` → `Backtest | Optimization | Live`
  - `sig`, `risk` → `Backtest | Live`
  - `bar`, `ind` → `Backtest`
  - `tick`, `bar.mut`, `ind.mut` → `Backtest` (opt-in)
  - `run.start`, `run.end`, `err`, `warn` → `Backtest | Optimization | Live`

### IEventBus Interface

- [x] `IEventBus` interface in Domain with `void Emit<T>(T evt) where T : IBacktestEvent`
- [x] Call sites emit unconditionally — bus handles all filtering internally
- [x] No filtering logic leaks to call sites

### EventBus Implementation

- [x] `EventBus` in Application layer
- [x] Checks `ExportMode.HasFlag(currentRunMode)` before serialization — non-matching events silently dropped
- [x] Checks `DataSubscription.IsExportable` for `bar`, `bar.mut`, `ind`, `ind.mut` events — drops events from non-exportable subscriptions
- [x] Serializes each surviving event to JSON **once** (not per-sink)
- [x] Fans out serialized payload to all registered `ISink` instances
- [x] `ISink` interface defined (receives serialized event bytes/string)

### NullEventBus

- [x] `NullEventBus` implementation — all `Emit` calls are no-ops
- [x] Zero allocation on the emit path
- [x] Used for optimization runs and normal backtests that don't need event export

### Tests

- [x] Unit tests: each event type serializes/deserializes correctly with compact field names
- [x] Unit tests: `ExportMode` filtering — events dropped when mode doesn't match
- [x] Unit tests: `DataSubscription.IsExportable` filtering for bar/ind events
- [x] Unit tests: fan-out — single event dispatched to multiple sinks
- [x] Unit tests: `NullEventBus.Emit()` is a no-op (no allocations, no side effects)
- [x] All existing tests pass unchanged (`dotnet test` green)

### Non-Functional

- [x] No new NuGet packages introduced
- [x] Event records are `readonly record struct` or `sealed record` — no unnecessary heap pressure
- [x] Sequence number (`sq`) is monotonic per-run, assigned by the bus (not by emitters)
