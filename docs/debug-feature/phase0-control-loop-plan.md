# Debug Session Control Architecture

## Context

The debug-feature-requirements-v2.md spec describes a step-through debugger for backtests: pause, step to next bar, step to next trade, continue, etc. The existing `BacktestEngine.Run()` is a tight synchronous `while(true)` loop with no external control hooks. We need to make this loop pausable/resumable from HTTP requests, without breaking the existing synchronous backtest path or adding overhead to normal (non-debug) runs.

This plan covers the **execution control mechanism only** — the pause/step/continue infrastructure. Event serialization (JSONL, IEventBus) and WebSocket transport are out of scope.

## Design: Injected Debug Probe

**Core idea**: Add an `IDebugProbe` callback interface to Domain. The engine calls it at bar boundaries. For debug sessions, an Application-layer implementation blocks the engine thread via `ManualResetEventSlim` until an HTTP command arrives. For normal runs, a null-object implementation (`IsActive = false`) is injected — the engine skips all probe calls via a cached boolean, zero overhead.

### Why this approach (not alternatives)

- **Wrapper service**: Can't control the loop from outside without exposing engine internals or duplicating the iteration logic.
- **Make Run() async**: Breaking change to all callers including optimization's `Parallel.ForEachAsync`. Unnecessary — only debug sessions need gating.
- **Channel/SemaphoreSlim**: ManualResetEventSlim is the natural primitive for a gate pattern (reset → wait → set from another thread). Engine stays sync, HTTP stays async.

### Stepping granularity

Initial implementation gates at **bar boundaries only**. Each pause point means "all processing for this bar is complete" (OnBarStart → order fills → SL/TP → OnBarComplete). The HTTP response includes fill count for that bar, so `next_trade` steps bar-by-bar until a bar produces a fill. Fine-grained per-event stepping comes later with IEventBus.

---

## Implementation

### 1. Domain types (`src/AlgoTradeForge.Domain/Engine/`)

**New files:**

- `IDebugProbe.cs` — interface with `bool IsActive`, `OnRunStart()`, `OnBarProcessed(DebugSnapshot)`, `OnRunEnd()`
- `DebugSnapshot.cs` — `readonly record struct` with sequence number, timestamp, subscription index, fill count in this bar, portfolio equity
- `NullDebugProbe.cs` — singleton null-object, `IsActive = false`

```csharp
public interface IDebugProbe
{
    bool IsActive { get; }
    void OnRunStart();
    void OnBarProcessed(DebugSnapshot snapshot);
    void OnRunEnd();
}

public readonly record struct DebugSnapshot(
    long SequenceNumber,
    long TimestampMs,
    int SubscriptionIndex,
    bool IsExportableSubscription,
    int FillsThisBar,
    long PortfolioEquity);
```

### 2. BacktestEngine.cs changes (minimal, additive)

- Add optional parameter: `IDebugProbe? probe = null`
- Cache `var probeActive = probe.IsActive;` + add `sequenceNumber` counter
- 3 guarded call sites in `Run()` only — **no changes to private methods**:

```
After strategy.OnInit()          → if (probeActive) probe.OnRunStart();
After each bar's full processing → if (probeActive) probe.OnBarProcessed(snapshot);
After loop exit                  → if (probeActive) probe.OnRunEnd();
```

Existing callers (`RunBacktestCommandHandler`, optimization handler, all tests) pass no probe argument → defaults to `NullDebugProbe.Instance` → `probeActive = false` → all probe branches are dead code.

### 3. GatingDebugProbe (`src/AlgoTradeForge.Application/Debug/`)

The core synchronization class. Implements `IDebugProbe` with `ManualResetEventSlim` gate:

- Starts gated (session begins paused)
- `OnBarProcessed()` → stores snapshot, notifies HTTP waiter, evaluates break condition → if should break, resets gate and blocks
- `SendCommandAsync(DebugCommand, CancellationToken)` → sets break condition, opens gate, returns `Task<DebugSnapshot>` that completes at next break
- Break conditions: `Always` (next), `OnExportableBar`, `OnFillBar` (next_trade), `AtSequence(n)`, `AtTimestamp(ms)`, `Never` (continue)

**Thread safety**: `_breakCondition` is written by the HTTP thread (inside `lock`) and read by the engine thread (no lock). Use `Volatile.Write` on set and `Volatile.Read` on read for cross-platform memory model correctness. On x86 this is a no-op but ensures correctness on ARM.

**Continue → Pause race**: When `Continue` is active (`_breakCondition = Never`), the engine runs freely. A subsequent `Pause` sets `_breakCondition = Always` via `Volatile.Write`. The engine sees this on its next `OnBarProcessed()` call (at most 1 bar later — microseconds). The engine always re-checks the break condition after each bar, so `Pause` is guaranteed to take effect.

Thread model:
```
Engine thread (LongRunning)          HTTP thread (async)
  │                                    │
  │ probe.OnBarProcessed(snap)         │
  │ → NotifyWaiter(snap) ───────────→  │ ← awaiting TCS completes
  │ → ShouldBreak? yes                 │
  │ → _gate.Reset()                    │
  │ → _gate.Wait() ← BLOCKED          │
  │                                    │ POST /commands { "next_bar" }
  │                                    │ → probe.SendCommandAsync(NextBar)
  │                     ←────────────  │   → Volatile.Write(_breakCondition)
  │                                    │   → _gate.Set()
  │ ← UNBLOCKED                       │   → await new TCS
  │ continues processing...            │
```

### 4. Debug commands (`src/AlgoTradeForge.Application/Debug/`)

```csharp
public abstract record DebugCommand
{
    public sealed record Continue : DebugCommand;
    public sealed record Next : DebugCommand;           // next exportable bar
    public sealed record NextBar : DebugCommand;        // any bar
    public sealed record NextTrade : DebugCommand;      // bar with fills > 0
    public sealed record RunToSequence(long Sq) : DebugCommand;
    public sealed record RunToTimestamp(long Ms) : DebugCommand;
    public sealed record Pause : DebugCommand;
}
```

### 5. Session management (`src/AlgoTradeForge.Application/Debug/`)

- `DebugSession` — owns `GatingDebugProbe`, tracks `Task<BacktestResultDto> RunTask`, metadata
- `IDebugSessionStore` + `InMemoryDebugSessionStore` — `ConcurrentDictionary<Guid, DebugSession>`, singleton
- `StartDebugSessionCommandHandler` — reuses setup logic pattern from `RunBacktestCommandHandler`, creates session, launches engine on `Task.Factory.StartNew(..., LongRunning)`, returns session ID immediately (engine is paused)
- `SendDebugCommandHandler` — looks up session, calls `probe.SendCommandAsync()`, returns snapshot DTO

### 6. HTTP endpoints (`src/AlgoTradeForge.WebApi/Endpoints/DebugEndpoints.cs`)

```
POST   /api/debug-sessions                    → start session (returns { sessionId })
POST   /api/debug-sessions/{id}/commands       → send command (returns snapshot)
GET    /api/debug-sessions/{id}                → session status + last snapshot
DELETE /api/debug-sessions/{id}                → terminate (disposes probe, cancels engine)
```

### 7. DI registration

- `DependencyInjection.cs`: register `IDebugSessionStore`, both command handlers
- `Program.cs`: `app.MapDebugEndpoints()`

---

## Files to modify

| File | Change |
|------|--------|
| `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs` | Add `IDebugProbe?` param + 3 guarded call sites |
| `src/AlgoTradeForge.Application/DependencyInjection.cs` | Register debug handlers + session store |
| `src/AlgoTradeForge.WebApi/Program.cs` | Map debug endpoints |

## New files

| File | Layer |
|------|-------|
| `src/AlgoTradeForge.Domain/Engine/IDebugProbe.cs` | Domain |
| `src/AlgoTradeForge.Domain/Engine/DebugSnapshot.cs` | Domain |
| `src/AlgoTradeForge.Domain/Engine/NullDebugProbe.cs` | Domain |
| `src/AlgoTradeForge.Application/Debug/DebugCommand.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/GatingDebugProbe.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/DebugSession.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/IDebugSessionStore.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/InMemoryDebugSessionStore.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/StartDebugSessionCommand.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/StartDebugSessionCommandHandler.cs` | Application |
| `src/AlgoTradeForge.Application/Debug/SendDebugCommandHandler.cs` | Application |
| `src/AlgoTradeForge.WebApi/Endpoints/DebugEndpoints.cs` | WebApi |
| `src/AlgoTradeForge.WebApi/Contracts/DebugCommandRequest.cs` | WebApi |
| `tests/AlgoTradeForge.Domain.Tests/Engine/DebugProbeTests.cs` | Tests |
| `tests/AlgoTradeForge.Application.Tests/Debug/GatingDebugProbeTests.cs` | Tests |
| `tests/AlgoTradeForge.Application.Tests/Debug/DebugSessionHandlerTests.cs` | Tests |

## Verification

1. **Existing tests pass unchanged** — `dotnet test` with no modifications to test code
2. **Domain probe tests** — verify `NullDebugProbe` causes no calls; mock probe receives `OnRunStart` → N × `OnBarProcessed` → `OnRunEnd` in correct order with correct sequence numbers
3. **GatingDebugProbe thread tests** — two-thread tests: engine thread calls probe methods, test thread sends commands, verify blocking/unblocking and snapshot delivery
4. **Integration test** — start debug session via HTTP, step through 3 bars with `next_bar`, verify sequence numbers increment, send `continue`, verify session completes with BacktestResult
5. **Performance** — existing 500K-bar perf test must not regress (probe branch is dead code on normal path)
