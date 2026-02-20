# Phase 1 — Event Model & Bus Core

**Parent:** `docs/debug-feature/requirements.md` §11.2
**Depends on:** Phase 0 (done)
**Unlocks:** Phase 2, Phase 7

---

## Acceptance Criteria

### Event Type Records

- [ ] All event types from §2.2 defined as C# records implementing `IBacktestEvent`:
  - [ ] `BarEvent` — bar closed (`bar`)
  - [ ] `BarMutEvent` — open bar mutated (`bar.mut`)
  - [ ] `IndEvent` — indicator computed for closed bar (`ind`)
  - [ ] `IndMutEvent` — indicator recomputed on mutation (`ind.mut`)
  - [ ] `SigEvent` — strategy signal generated (`sig`)
  - [ ] `RiskEvent` — risk check performed (`risk`)
  - [ ] `OrdPlaceEvent` — order submitted (`ord.place`)
  - [ ] `OrdFillEvent` — order filled (`ord.fill`)
  - [ ] `OrdCancelEvent` — order cancelled (`ord.cancel`)
  - [ ] `OrdRejectEvent` — order rejected (`ord.reject`)
  - [ ] `PosEvent` — position changed (`pos`)
  - [ ] `RunStartEvent` — run began with config snapshot (`run.start`)
  - [ ] `RunEndEvent` — run completed with summary (`run.end`)
  - [ ] `ErrEvent` — error (`err`)
  - [ ] `WarnEvent` — warning (`warn`)
  - [ ] `TickEvent` — new tick received (`tick`) — opt-in, `ExportMode = Backtest`
- [ ] Each record carries the canonical envelope fields: `ts` (ISO 8601 UTC), `sq` (uint64 monotonic), `_t` (event type string), `src` (emitting component), `d` (typed payload)
- [ ] Event types live in Domain layer (no infrastructure dependencies)

### ExportMode

- [ ] `[Flags] enum ExportMode { Backtest = 1, Optimization = 2, Live = 4 }` defined
- [ ] Each event type annotated with its default `ExportMode` per §2.3 table:
  - `ord.*`, `pos` → `Backtest | Optimization | Live`
  - `sig`, `risk` → `Backtest | Live`
  - `bar`, `ind` → `Backtest`
  - `tick`, `bar.mut`, `ind.mut` → `Backtest` (opt-in)
  - `run.start`, `run.end`, `err`, `warn` → `Backtest | Optimization | Live`

### IEventBus Interface

- [ ] `IEventBus` interface in Domain with `void Emit<T>(T evt) where T : IBacktestEvent`
- [ ] Call sites emit unconditionally — bus handles all filtering internally
- [ ] No filtering logic leaks to call sites

### EventBus Implementation

- [ ] `EventBus` in Application layer
- [ ] Checks `ExportMode.HasFlag(currentRunMode)` before serialization — non-matching events silently dropped
- [ ] Checks `DataSubscription.IsExportable` for `bar`, `bar.mut`, `ind`, `ind.mut` events — drops events from non-exportable subscriptions
- [ ] Serializes each surviving event to JSON **once** (not per-sink)
- [ ] Fans out serialized payload to all registered `ISink` instances
- [ ] `ISink` interface defined (receives serialized event bytes/string)

### NullEventBus

- [ ] `NullEventBus` implementation — all `Emit` calls are no-ops
- [ ] Zero allocation on the emit path
- [ ] Used for optimization runs and normal backtests that don't need event export

### Tests

- [ ] Unit tests: each event type serializes/deserializes correctly with compact field names
- [ ] Unit tests: `ExportMode` filtering — events dropped when mode doesn't match
- [ ] Unit tests: `DataSubscription.IsExportable` filtering for bar/ind events
- [ ] Unit tests: fan-out — single event dispatched to multiple sinks
- [ ] Unit tests: `NullEventBus.Emit()` is a no-op (no allocations, no side effects)
- [ ] All existing tests pass unchanged (`dotnet test` green)

### Non-Functional

- [ ] No new NuGet packages introduced
- [ ] Event records are `readonly record struct` or `sealed record` — no unnecessary heap pressure
- [ ] Sequence number (`sq`) is monotonic per-run, assigned by the bus (not by emitters)
