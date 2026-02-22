# Research: Long-Running Operations Flow

**Feature**: 009-long-running-ops
**Date**: 2026-02-22

## R1: Background Processing Pattern

**Decision**: Use `Task.Run()` with `CancellationTokenSource` for background execution, storing progress in a `ConcurrentDictionary<Guid, T>` singleton.

**Rationale**:
- The constitution requires "a job framework with persistence (Hangfire, Quartz.NET, or similar)" for background jobs. However, Principle VI (Simplicity & YAGNI) overrides this for a single-user, single-node local system.
- `InMemoryDebugSessionStore` (Application layer) already establishes the pattern: `ConcurrentDictionary<Guid, T>` with `Lock` for capacity control.
- `BacktestEngine.Run()` is synchronous and CPU-bound — `Task.Run()` offloads it to the thread pool correctly.
- `CancellationToken` is already wired through the full handler → engine → main loop chain.

**Alternatives considered**:
- **Hangfire / Quartz.NET**: Overkill for single-user local tool. Adds persistence layer, dashboard, serialization constraints. Violates YAGNI.
- **BackgroundService (hosted service)**: Designed for long-lived singleton workers (like CandleIngestor), not request-scoped fire-and-forget jobs. Would require a message queue to accept commands.
- **Channel\<T\> producer/consumer**: Adds unnecessary indirection. A single `Task.Run()` per request is simpler.

## R2: In-Memory Progress Store

**Decision**: Create `IRunProgressStore` interface + `InMemoryRunProgressStore` implementation in the Application layer using `ConcurrentDictionary<Guid, RunProgress>`.

**Rationale**:
- `Microsoft.Extensions.Caching.Memory` is already referenced in Domain.csproj but `IMemoryCache` is designed for cache eviction scenarios with sliding/absolute expiration. We need deterministic key-value storage where entries are explicitly managed.
- `ConcurrentDictionary` matches the existing `InMemoryDebugSessionStore` pattern.
- Progress entries are created on submission, updated during execution, and removed after the completed results are persisted to SQLite. This prevents unbounded memory growth.

**Alternatives considered**:
- **IMemoryCache**: Eviction policies could remove in-flight progress data unexpectedly. Manual entry management is more explicit.
- **Redis**: Not justified for single-node local system.

## R3: Progress Tracking Mechanism — Backtest

**Decision**: Implement a custom `IEventBus` sink that increments a counter on each `BarEvent` emission, writing to the progress store.

**Rationale**:
- `BacktestEngine.Run()` already emits `BarEvent` via `IEventBus` for every bar processed.
- The `IEventBus.Emit<T>()` method is synchronous and called on the engine's hot loop — the counter increment must be cheap (`Interlocked.Increment`).
- Total bar count is known upfront from `TimeSeries<Int64Bar>.Count` after data loading.

**Alternatives considered**:
- **IProgress\<T\> callback**: Would require modifying the Domain engine signature. Using existing `IEventBus` avoids Domain changes.
- **Polling engine state**: Engine is stateless by design; no internal state to poll.

## R4: Progress Tracking Mechanism — Optimization

**Decision**: Use `Interlocked.Increment` on a shared counter inside the `Parallel.ForEachAsync` body, updating the progress store after each trial completes (or fails).

**Rationale**:
- The optimization handler already uses `ConcurrentBag` for thread-safe result collection within `Parallel.ForEachAsync`.
- Adding an `Interlocked.Increment` on a shared `long` counter is negligible cost.
- Failed trials increment a separate failure counter.
- Progress store is updated atomically per trial completion.

**Alternatives considered**:
- **Per-batch updates (every N trials)**: Adds complexity; per-trial updates with `Interlocked` are fast enough.

## R5: Error Storage on Trial/Backtest Records

**Decision**: Add `string? ErrorMessage` and `string? ErrorStackTrace` fields to `BacktestRunRecord`. Failed trials have zero/default `PerformanceMetrics` and empty equity curves.

**Rationale**:
- Clarification session confirmed: save all trials including failed ones with error details.
- Metrics fields use value types (`double`, `decimal`) with natural zero defaults.
- SQLite schema needs two new nullable TEXT columns on `backtest_runs` table.
- The existing `PerformanceMetrics` record stays unchanged — zeros are semantically correct for failed trials.

**Alternatives considered**:
- **Separate error table**: Over-normalized for this use case; error fields directly on the record are simpler.
- **Nullable PerformanceMetrics**: Would require making every consumer null-aware. Zero metrics are safer.

## R6: API Contract Design — Status Endpoints

**Decision**:
- POST endpoints return `202 Accepted` with `{ id, total, status }`.
- New `GET /api/backtests/{id}/status` and `GET /api/optimizations/{id}/status` endpoints return progress + nullable result.
- New `POST /api/backtests/{id}/cancel` and `POST /api/optimizations/{id}/cancel` endpoints for cancellation.

**Rationale**:
- Existing `GET /api/backtests/{id}` retrieves completed runs from SQLite. The new `/status` endpoint checks the in-memory progress store first, then falls back to the persistence layer.
- `202 Accepted` is the correct HTTP semantics for accepted-but-not-yet-completed requests.
- Cancel as POST (not DELETE) because it's an action on the resource, not resource removal.

**Alternatives considered**:
- **Overloading existing GET endpoint**: Would mix volatile in-memory state with persistent storage in a confusing way. Separate `/status` is clearer.
- **WebSocket for progress**: Out of scope per spec assumptions (polling model chosen).

## R7: Frontend Polling Strategy

**Decision**: Use TanStack Query's `refetchInterval` option for polling. Create dedicated hooks `useBacktestStatus(id)` and `useOptimizationStatus(id)` that poll while the run status is non-terminal.

**Rationale**:
- TanStack Query v5 (`^5.90.21`) supports `refetchInterval` natively — can be a static value or a function that returns `false` to stop.
- Pattern: `refetchInterval: (query) => query.state.data?.status === 'completed' ? false : 5000`
- Existing hooks (`useBacktestDetail`, `useOptimizationDetail`) remain unchanged for viewing persisted results.
- Current 30-second `DEFAULT_TIMEOUT_MS` in api-client needs adjustment — submission responses are fast (< 2s) but the timeout config is global.

**Alternatives considered**:
- **Manual setInterval**: Loses TanStack Query's caching, deduplication, and error retry benefits.
- **WebSocket**: Out of scope.
