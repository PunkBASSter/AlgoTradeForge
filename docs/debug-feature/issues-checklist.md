# Code Review Issues Checklist

**Branch:** `007-debug-control-in-loop`
**Review date:** 2026-02-21
**Tests:** 449/449 passing (366 Domain + 83 Infrastructure)

---

## Critical (3)

- [x] **C1 — Non-atomic snapshot read in `GatingDebugProbe.OnEventEmitted`**
  - File: `src/AlgoTradeForge.Application/Debug/GatingDebugProbe.cs:69`
  - `_lastSnapshot` (32-byte struct) read without lock, but `OnBarProcessed` writes under lock. Either lock the read or document the single-threaded invariant explicitly.

- [x] **C2 — No authentication on WebSocket debug endpoint**
  - File: `src/AlgoTradeForge.WebApi/Endpoints/DebugWebSocketHandler.cs`
  - `/api/debug-sessions/{id}/ws` has no `.RequireAuthorization()`. Session GUID is the only protection. Add auth or bind to localhost only.
  - **Fix:** Added `LocalhostOnlyFilter` endpoint filter to both REST group and WebSocket endpoint. Non-loopback connections get 403.

- [x] **C3 — Application-layer tests in `Domain.Tests`**
  - 9+ test files test Application-layer classes (`BreakConditionTests`, `DebugCommandParsingTests`, `DebugSessionHandlerTests`, `InMemoryDebugSessionStoreTests`, `WebSocketSinkTests`, `EventBusTests`, `EventSerializationTests`, `ExportModeFilteringTests`, `RunIdentityTests`, `IndicatorFactoryTests`) but live in the Domain test project.
  - Also: `Application.csproj` has `InternalsVisibleTo` targeting `Domain.Tests` rather than `Application.Tests`.
  - **Fix:** Created `AlgoTradeForge.Application.Tests` project. Moved 13 files (12 test classes + `DuplexStreamPair` utility) via `git mv`. Updated all namespaces. Fixed `InternalsVisibleTo` to target `Application.Tests`. Added project to solution.

---

## Warning — Thread Safety & Concurrency (5)

- [x] **W1 — `ManualResetEventSlim` never disposed**
  - File: `src/AlgoTradeForge.Application/Debug/GatingDebugProbe.cs:148-158`
  - `_gate` kernel handle leaks. Add `DisposeGate()` called from `DebugSession.DisposeAsync()` after awaiting `RunTask`.

- [x] **W2 — Race between `Dispose()` and `SendCommandAsync()`**
  - File: `src/AlgoTradeForge.Application/Debug/GatingDebugProbe.cs`
  - Returns zeroed `DebugSnapshot` instead of throwing `ObjectDisposedException`.

- [x] **W3 — TOCTOU race on max session count**
  - File: `src/AlgoTradeForge.Application/Debug/InMemoryDebugSessionStore.cs:19`
  - Two concurrent `Create()` calls can both pass the count check. Use a lock around check-and-add.

- [x] **W4 — `WebSocketSink` field access not synchronized**
  - File: `src/AlgoTradeForge.Application/Events/WebSocketSink.cs:36-44`
  - `Attach`/`Detach`/`Write` access `_webSocket` without lock or `volatile`.

- [x] **W5 — `JsonlFileSink.Write` not thread-safe**
  - File: `src/AlgoTradeForge.Infrastructure/Events/JsonlFileSink.cs`
  - Interleaved `Write(data)` + `Write(newline)` could corrupt JSONL output if ever called concurrently.

---

## Warning — Resource Management (4)

- [x] **W6 — `DebugSession` synchronous `Dispose()` leaks resources**
  - File: `src/AlgoTradeForge.Application/Debug/DebugSession.cs:58-64`
  - `EventSink`, `WebSocketSink`, `Cts` not cleaned up in synchronous path. Consider removing `IDisposable` and only supporting `IAsyncDisposable`.

- [x] **W7 — No double-dispose guard in `DebugSession`**
  - File: `src/AlgoTradeForge.Application/Debug/DebugSession.cs`
  - `Cts.Dispose()` called twice throws `ObjectDisposedException`. Add a `_disposed` flag.

- [x] **W8 — `WebSocketSink` synchronous `Dispose()` disposes CTS while send loop runs**
  - File: `src/AlgoTradeForge.Application/Events/WebSocketSink.cs:128-133`
  - `_sendCts.Dispose()` called without waiting for `_sendLoop` to finish.

- [x] **W9 — Missing `Pooling=False` in `SqliteTradeDbWriterTests`**
  - File: `tests/AlgoTradeForge.Infrastructure.Tests/Events/SqliteTradeDbWriterTests.cs`
  - 6 SQLite connection strings (lines 88, 114, 149, 204, 226, 262) lack `Pooling=False`. Can cause flaky Windows CI failures.

---

## Warning — Correctness (6)

- [ ] **W10 — `RunEndEvent` not emitted on cancellation/exception**
  - File: `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs:168-177`
  - `probe.OnRunEnd()` IS in `finally`, but `RunEndEvent` emission is not. Asymmetric lifecycle.

- [ ] **W11 — `GetString()!` null-forgiving on event type field**
  - File: `src/AlgoTradeForge.Application/Debug/DebugCommandParser.cs:37`
  - If JSON `_t` field is `null`, passes `null` into `DebugCommand.NextType`. Add null check.

- [ ] **W12 — `ParseCommand` in `DebugEndpoints` duplicates `DebugCommandParser`**
  - File: `src/AlgoTradeForge.WebApi/Endpoints/DebugEndpoints.cs:144-162`
  - REST path missing `next_signal`, `next_type`, `set_export` commands. Delete duplicate and delegate to `DebugCommandParser`.

- [ ] **W13 — Second WebSocket connection throws after accept**
  - File: `src/AlgoTradeForge.WebApi/Endpoints/DebugWebSocketHandler.cs`
  - `Attach` throws if already connected, but only after `AcceptWebSocketAsync`. Check before accepting and return 409.

- [ ] **W14 — Stale channel messages on WebSocket reconnect**
  - File: `src/AlgoTradeForge.WebApi/Endpoints/DebugWebSocketHandler.cs`
  - After `DetachAsync`, channel may contain stale messages. Drain or replace channel on `Attach`.

- [ ] **W15 — `DeleteRun` missing `PRAGMA busy_timeout=5000`**
  - File: `src/AlgoTradeForge.Infrastructure/Events/SqliteTradeDbWriter.cs`
  - Inconsistent with `EnsureSchema` and `InsertRun`. Can cause sporadic `SQLITE_BUSY` failures during `RebuildFromJsonl`.

---

## Warning — Performance (2)

- [ ] **W16 — `List<long>` allocated per `ProcessPendingOrders` call**
  - File: `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs:196`
  - GC pressure on long backtests. Consider pooling or reusing a field-level list.

- [ ] **W17 — `ArrayBufferWriter<byte>` allocated every `Emit` call**
  - File: `src/AlgoTradeForge.Application/Events/EventBus.cs:56`
  - Hot-path GC pressure. Pool or reuse as `[ThreadStatic]` since engine is single-threaded.

---

## Warning — Design (2)

- [ ] **W18 — Direct cast to concrete `EventBus` in WebSocket handler**
  - File: `src/AlgoTradeForge.WebApi/Endpoints/DebugWebSocketHandler.cs`
  - `SetExport` command casts `session.EventBus` to `EventBus`. Add `SetMutationsEnabled` to `IEventBus` or create `IMutableEventBus`.

- [ ] **W19 — `OnEventEmitted` declared but never called**
  - File: `src/AlgoTradeForge.Domain/Engine/IDebugProbe.cs:25`
  - Dead API surface. Remove until actually wired up (YAGNI).

---

## Warning — Test Quality (3)

- [ ] **W20 — Duplicate helper classes across test files**
  - `CapturingEventBus` (x3), `RecordingSink` (x2), `BuyOnFirstBarStrategy` (x2). Extract to shared `TestUtilities/`.

- [ ] **W21 — `session.Dispose()` not in finally blocks**
  - File: `tests/AlgoTradeForge.Domain.Tests/Engine/DebugSessionHandlerTests.cs`
  - Resource leak on assertion failure. Use `using` or try/finally.

- [ ] **W22 — `Task.Delay` for synchronization in WebSocket tests**
  - Files: `DebugWebSocketIntegrationTests.cs`, `WebSocketSinkTests.cs`
  - Fixed `Task.Delay(100-200ms)` flaky on slow CI runners. Use polling loops with timeout.

---

## Info (12)

- [ ] **I1** — `busActive` via `is not NullEventBus` is fragile if alternate no-op bus introduced (`BacktestEngine.cs`)
- [ ] **I2** — If `bus.Emit()` throws in the error handler, original exception is lost (`BacktestEngine.cs:156`)
- [ ] **I3** — `Submit` returns `order.Id` which may be `0` (engine sentinel) if strategy doesn't pre-assign (`BacktestOrderContext.cs:34`)
- [ ] **I4** — `GetPendingForAsset` returns shared mutable `_pendingBuffer` — second call invalidates first (`OrderQueue.cs:23`)
- [ ] **I5** — `PortfolioEquity` returned as raw `long` — inconsistent with scaled `BacktestResultDto` (`DebugSessionDto.cs`)
- [ ] **I6** — `InternalsVisibleTo` targets `Domain.Tests` rather than `Application.Tests` (`Application.csproj:17`)
- [ ] **I7** — Wall-clock `DateTimeOffset.UtcNow` used as event timestamp in backtests (`EmittingIndicatorDecorator.cs:36`)
- [ ] **I8** — No cleanup of orphaned session if `PrepareAsync` throws after `Create()` (`StartDebugSessionCommandHandler.cs`)
- [ ] **I9** — Non-standard timeframes (e.g., 90s) truncate to nearest unit `1m` (`TimeFrameFormatter.cs`)
- [ ] **I10** — `++_sequence` not thread-safe; fine if single-threaded but needs documentation (`EventBus.cs:54`)
- [ ] **I11** — `SignalEvent.Direction` is `string` — consider a strongly-typed enum (`SignalEvents.cs`)
- [ ] **I12** — No `WebSocketOptions.KeepAliveInterval` configured — debug sessions paused >2min may drop (`Program.cs`)
