# Research: Optimization Task Queue

## R1: Queue Implementation — Channel\<T\> vs Alternatives

**Decision**: Use `Channel<ComputeTask>.CreateUnbounded()` with `SingleReader = true`

**Rationale**: The codebase already uses this pattern in two places:
- `LiveOrderContext.cs` (lines 31-34): `Channel.CreateUnbounded<OrderRequest>(new UnboundedChannelOptions { SingleReader = true })`
- `BinanceLiveConnector.cs` (lines 65-66): `Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true })`

Both use `await foreach (var item in channel.Reader.ReadAllAsync(ct))` for consumption, `Writer.TryWrite()` for publishing, and `Writer.TryComplete()` for shutdown. This matches our requirements exactly: single consumer, multiple producers (API endpoints), and graceful shutdown.

**Alternatives considered**:
- `ConcurrentQueue<T>`: No async notification — would require polling. Channel provides `WaitToReadAsync()` natively.
- `BlockingCollection<T>`: Thread-blocking, not async-friendly. Incompatible with the `BackgroundService` async pattern.
- External message broker (RabbitMQ, etc.): Overkill for single-node ephemeral queue. Adds infrastructure dependency.

## R2: Consumer Pattern — BackgroundService

**Decision**: Implement consumer as `BackgroundService` registered via `AddHostedService<>()`

**Rationale**: Existing pattern in `SqliteIndexMaintenanceService.cs` (registered at `Program.cs:116`). The BackgroundService lifecycle integrates with ASP.NET Core host shutdown, ensuring graceful cancellation via `stoppingToken`.

**Key behaviors**:
- `ExecuteAsync(CancellationToken stoppingToken)` runs for the lifetime of the application
- `stoppingToken` fires on host shutdown → consumer completes current task, marks remaining as cancelled
- Singleton lifetime guaranteed by DI

## R3: Handler Refactoring Strategy

**Decision**: Extract execution logic from handlers into standalone executor classes. Handlers become thin "prepare + enqueue" wrappers.

**Rationale**: The current handlers do two things in `HandleAsync()`:
1. **Prepare** (sync): validate strategy, resolve axes, estimate combinations, insert DB placeholders
2. **Execute** (background): `Task.Factory.StartNew(() => RunGroupBruteForceAsync(...))` — the actual parallel computation

The refactoring splits these cleanly:
- **Handlers** keep steps 1 (prepare) + enqueue task descriptors to the channel
- **Executors** receive the step 2 logic, called by the queue consumer

**Key files to refactor**:
- `RunGroupOptimizationCommandHandler.cs`: Extract `RunGroupBruteForceAsync()` (lines 233-700+) and `RunSingleDssBruteForceAsync()` into `OptimizationTaskExecutor`
- `RunGroupValidationCommandHandler.cs`: Extract `RunSingleValidationAsync()` (lines 169-346) into `ValidationTaskExecutor`
- `RunOptimizationCommandHandler.cs`: Extract `RunOptimizationAsync()` into the same executor (single-DSS = group with 1 DSS)
- `RunGeneticOptimizationCommandHandler.cs`: Extract genetic execution similarly

**Data loading**: Currently done in `HandleAsync()` before launching the background task (shared data cache). In the new model, data loading moves to the executor (called by consumer at execution time) to avoid holding large datasets in memory while tasks wait in the queue.

## R4: Trial Cache Handoff Between Optimization and Validation

**Decision**: Consumer manages a per-DSS trial cache dictionary. After optimization completes for a DSS, the consumer holds the `BoundedTrialQueue` results and injects them into the validation task for the same DSS.

**Rationale**: Currently, `RunGroupValidationCommandHandler` loads trials from SQLite (line 270-277):
```csharp
var optimization = await runRepository.GetOptimizationByIdAsync(
    optimizationRun.Id, includeEquityCurves: false, includeTrials: true, ct);
```

This is wasteful when optimization just completed and trials are still in memory. The queue consumer can pass the `IReadOnlyList<BacktestRunRecord>` directly, skipping the DB round-trip entirely.

**Cache lifecycle**:
1. Optimization completes → consumer stores trial results in `Dictionary<(Guid jobId, int dssIndex), IReadOnlyList<BacktestRunRecord>>`
2. Validation starts → consumer passes cached trials to the executor (FR-003)
3. Both complete (or validation skipped) → consumer removes cache entry (FR-012)

**Standalone validation** (FR-013): No cache available — falls back to loading from DB (existing behavior).

## R5: I/O During Handoff

**Decision**: Fire-and-forget `Task.Run()` for DB persistence when transitioning from optimization to validation within the same DSS.

**Rationale**: The queue processes one compute task at a time, but I/O (SQLite writes) is not CPU-intensive. Persisting optimization results concurrently with the validation compute task is safe because:
- SQLite writes don't compete for CPU cores
- Validation reads from in-memory cache, not from DB
- If persistence fails, the in-progress task is unaffected; error is logged and retry can happen later

**Sequence**: Opt completes → consumer starts `Task.Run(SaveOptimizationAsync)` + immediately dequeues next task (validation) → both run concurrently.

## R6: Cancellation Architecture

**Decision**: Extend existing `InMemoryRunCancellationRegistry` pattern. Each executing task gets its own `CancellationTokenSource`. Pending tasks can be cancelled by removing them from the channel (via task status update).

**Current pattern** (`InMemoryRunCancellationRegistry`):
- `Register(Guid id, CancellationTokenSource cts)` at task start
- `TryCancel(Guid id)` → calls `cts.Cancel()`
- `Remove(Guid id)` at task completion

**Extension for queue**:
- `ComputeTaskQueue` maintains a `ConcurrentDictionary<Guid, ComputeTask>` for all tasks (pending + in-progress)
- Cancelling a pending task: set status to `Cancelled`, consumer skips it when dequeued
- Cancelling in-progress task: calls registry's `TryCancel()` on the run ID
- Purge pending: iterate all pending tasks, set status to `Cancelled`
- Cascade: when optimization task cancelled/failed, find matching validation task and cancel it (FR-008)

## R7: Frontend Polling Pattern

**Decision**: New `useTaskQueue()` hook using TanStack Query with 2-second polling interval, following existing patterns.

**Rationale**: Existing hooks use dynamic backoff polling:
- `useOptimizationGroupStatus()` in `use-optimization-groups.ts`: 2s refetchInterval
- `useValidationGroupStatus()` in `use-validation-groups.ts`: 2s refetchInterval
- `useOptimizationStatus()` in `use-run-status.ts`: 5s base with exponential backoff

For the task queue, a fixed 2s interval is appropriate because:
- The queue is expected to always have active content when visible
- Backoff is unnecessary (queue panel only shown when tasks exist)
- 2s matches existing group status polling

**Polling stops**: When the queue is empty (all tasks completed/cancelled), polling can reduce to 10s or stop entirely.

## R8: Max Threads Implementation

**Decision**: Add `maxParallelism` parameter to the compute task. Consumer passes it to the executor, which uses it in `Partitioner.Create()` concurrency.

**Current behavior**: `RunGroupOptimizationCommandHandler` uses `command.MaxDegreeOfParallelism` (default -1 = CPU count). The `Partitioner.Create()` call uses `maxParallelism` partitions.

**New behavior**: Same logic, but the value comes from the task queue submission. `0` maps to `Environment.ProcessorCount`, positive values are clamped to `Environment.ProcessorCount`.
