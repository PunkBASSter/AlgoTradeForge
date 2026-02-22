# Data Model: Long-Running Operations Flow

**Feature**: 009-long-running-ops
**Date**: 2026-02-22

## Architecture: Three-Tier Hybrid Storage

Progress tracking uses three complementary stores, each chosen for its serialization and lifecycle characteristics:

| Concern | Storage | Key Pattern | Lifetime |
|---------|---------|-------------|----------|
| Progress counters + status + errors | `IDistributedCache` via `RunProgressCache` | `progress:{guid}` | Until completion + cleanup |
| RunKey → Guid dedup mapping | `IDistributedCache` via `RunProgressCache` | `runkey:{runKey}` | Until completion + cleanup |
| CancellationTokenSource (not serializable) | `InMemoryRunCancellationRegistry` (`ConcurrentDictionary`) | Guid key | Until completion + cleanup |
| Completed results | SQLite via `IRunRepository` (existing) | Guid PK | Permanent |

**Why three tiers?** `CancellationTokenSource` is not serializable and cannot live in `IDistributedCache`. Progress counters and dedup mappings are serializable and belong in `IDistributedCache` per Constitution v1.6.0. Completed results are persisted to SQLite as before.

**DI registration**: `builder.Services.AddDistributedMemoryCache()` in `Program.cs` — locally backed by `MemoryDistributedCache`; swappable to Redis via `AddStackExchangeRedisCache()` with zero code changes.

---

## New Types

### RunStatus (Enum — Application Layer)

File: `src/AlgoTradeForge.Application/Progress/RunStatus.cs`

Lifecycle state of a background operation.

| Value | Description |
|-------|-------------|
| Pending | Submitted but not yet started processing |
| Running | Actively processing |
| Completed | Finished successfully, results available |
| Failed | Encountered an unrecoverable error |
| Cancelled | User requested cancellation |

**State Transitions**:
```
Pending → Running → Completed
                  → Failed
         → Failed (startup failure)
         → Cancelled (user cancels before processing starts)
Running → Cancelled
```

### RunProgressEntry (Record — Application Layer)

File: `src/AlgoTradeForge.Application/Progress/RunProgressEntry.cs`

Unified serializable progress state stored in `IDistributedCache`. Replaces the separate `BacktestProgress` and `OptimizationProgress` classes from the original design.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier (same as BacktestRunRecord.Id) |
| Status | RunStatus | Current lifecycle state |
| Processed | long | Items processed so far — bars (backtest) or completed combinations (optimization) |
| Failed | long | Items that failed — 0 for backtests, failed trial count for optimizations |
| Total | long | Total items to process — total bars (backtest) or total combinations (optimization) |
| ErrorMessage | string? | Error description if the entire run failed |
| ErrorStackTrace | string? | Stack trace if the entire run failed |
| StartedAt | DateTimeOffset | When the run was submitted |

**Design decisions**:
- **No `Result` field**: Completed results come from SQLite via `IRunRepository`, not from cache. This keeps the cached entry small and serializable.
- **No `CancellationTokenSource` field**: CTS is not serializable; stored separately in `InMemoryRunCancellationRegistry`.
- **Unified `Processed`/`Failed`/`Total` counters**: Generic names that work for both backtests (Processed = bars) and optimizations (Processed = completed combinations, Failed = failed trials).
- **`long` counters**: Consistent with optimization's `TotalCombinations` (long) and avoids narrowing.
- **`int` bar counts in backtest types**: `BacktestSubmissionDto.TotalBars`, `BacktestStatusResponse.ProcessedBars`, and `BacktestStatusResponse.TotalBars` use `int` because the source value (`TimeSeries<Int64Bar>.Count` and `BacktestRunRecord.TotalBars`) is `int`. The unified `RunProgressEntry.Total` uses `long` for optimization compatibility; narrowing to `int` is safe for backtests since bar counts cannot exceed `int.MaxValue`.

**Serialization**: JSON via `System.Text.Json`. Stored in `IDistributedCache` under key `progress:{guid}`.

**Concurrency**: The `RunProgressEntry` record stored in cache is updated periodically by the handler (not on every bar). The handler maintains a local `long` counter incremented via `Interlocked.Increment` on each bar/trial, then flushes the current value to cache every **1 second** (hardcoded constant; avoids config complexity per Principle VI). The flush loop runs on the background `Task.Run` thread via `Stopwatch` comparison — if ≥1 second since last flush, serialize and write `RunProgressEntry` to cache.

### RunKeyBuilder (Static Class — Application Layer)

File: `src/AlgoTradeForge.Application/Progress/RunKeyBuilder.cs`

Generates a deterministic RunKey from command parameters for deduplication per Constitution v1.6.0.

```
static string Build(RunBacktestCommand cmd) → string
static string Build(RunOptimizationCommand cmd) → string
```

**Key format**: SHA256 hash of a canonical string representation of the command parameters. The canonical string includes: strategy name, strategy version, asset, exchange, time frame, start/end times, initial cash, commission, slippage, and sorted strategy parameters (for backtest) or sorted axes (for optimization).

**Example**: `RunKey = SHA256("SmaCrossover|1.0.0|BTCUSDT|Binance|1:00:00|2024-01-01|2024-12-31|10000|0.001|0|fastPeriod=10,slowPeriod=30")`

**Why SHA256?** Produces a fixed-length key regardless of parameter count. Avoids cache key length issues with many parameters.

### RunProgressCache (Class — Application Layer)

File: `src/AlgoTradeForge.Application/Progress/RunProgressCache.cs`

Typed wrapper around `IDistributedCache` for progress tracking and RunKey deduplication. Replaces the `IRunProgressStore` / `InMemoryRunProgressStore` from the original design.

**Constructor**: `RunProgressCache(IDistributedCache cache)`

**Progress operations**:
```
SetAsync(RunProgressEntry entry) → Task
    // Serializes entry to JSON, stores under "progress:{entry.Id}"

GetAsync(Guid id) → Task<RunProgressEntry?>
    // Retrieves and deserializes from "progress:{id}"

RemoveAsync(Guid id) → Task
    // Removes "progress:{id}"
```

**Dedup operations**:
```
TryGetRunIdByKeyAsync(string runKey) → Task<Guid?>
    // Reads "runkey:{runKey}", returns the Guid if present

SetRunKeyAsync(string runKey, Guid id) → Task
    // Stores id under "runkey:{runKey}"

RemoveRunKeyAsync(string runKey) → Task
    // Removes "runkey:{runKey}"
```

**Registration**: Singleton in DI container.

### IRunCancellationRegistry (Interface — Application Layer)

File: `src/AlgoTradeForge.Application/Progress/IRunCancellationRegistry.cs`

Manages `CancellationTokenSource` instances for active runs. Separate from cache because CTS is not serializable.

```
Register(Guid id, CancellationTokenSource cts) → void
TryCancel(Guid id) → bool
    // Returns true if found and Cancel() was called
TryGetToken(Guid id) → CancellationToken?
    // Returns the token if registered, null otherwise
Remove(Guid id) → void
```

### InMemoryRunCancellationRegistry (Class — Application Layer)

File: `src/AlgoTradeForge.Application/Progress/InMemoryRunCancellationRegistry.cs`

Implements `IRunCancellationRegistry` using `ConcurrentDictionary<Guid, CancellationTokenSource>`.

**Registration**: Singleton in DI container (`IRunCancellationRegistry` → `InMemoryRunCancellationRegistry`).

**Thread safety**: All operations are thread-safe via `ConcurrentDictionary`. `TryCancel` calls `CancellationTokenSource.Cancel()` which is itself thread-safe.

### BacktestSubmissionDto (Record — Application Layer)

File: `src/AlgoTradeForge.Application/Backtests/BacktestSubmissionDto.cs`

Returned by `RunBacktestCommandHandler` (replaces `BacktestResultDto` as the handler's `TResult`).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier for polling |
| TotalBars | int | Total bars to process |
| IsDedup | bool | True if this submission matched an existing active run |

### OptimizationSubmissionDto (Record — Application Layer)

File: `src/AlgoTradeForge.Application/Optimization/OptimizationSubmissionDto.cs`

Returned by `RunOptimizationCommandHandler` (replaces `OptimizationResultDto` as the handler's `TResult`).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier for polling |
| TotalCombinations | long | Total parameter combinations |
| IsDedup | bool | True if this submission matched an existing active run |

---

## Deduplication Flow

RunKey deduplication applies only to in-progress runs. Once a run completes (or fails/cancels), resubmitting the same parameters starts a new run.

### POST /api/backtests/ (or /api/optimizations/) — Pseudocode

```
1. Validate request (synchronous — return 400 on failure)
2. runKey = RunKeyBuilder.Build(command)
3. existingId = await RunProgressCache.TryGetRunIdByKeyAsync(runKey)
4. IF existingId has value:
   a. entry = await RunProgressCache.GetAsync(existingId)
   b. IF entry is not null AND entry.Status is Pending or Running:
      → return 202 Accepted { Id = existingId, Total = entry.Total, IsDedup = true }
   c. ELSE (stale mapping — completed/failed/cancelled/absent):
      → await RunProgressCache.RemoveRunKeyAsync(runKey)
      → fall through to step 5
5. Create new Guid, create RunProgressEntry (Pending), persist to cache
6. await RunProgressCache.SetRunKeyAsync(runKey, newId)
7. Register CTS in IRunCancellationRegistry
8. Start Task.Run() with background processing
9. return 202 Accepted { Id = newId, Total = totalCount, IsDedup = false }
```

### Background Task Completion — Pseudocode

```
1. On completion/failure/cancellation:
   a. Persist results to SQLite via IRunRepository
   b. Update RunProgressEntry status in cache (Completed/Failed/Cancelled)
   c. Remove RunKey mapping: await RunProgressCache.RemoveRunKeyAsync(runKey)
   d. Remove CTS: IRunCancellationRegistry.Remove(id)
   e. NOTE: progress entry stays in cache for polling; cleaned up in Phase 7
```

---

## Modified Types

### BacktestRunRecord (Application Layer — Persistence)

**New fields**:

| Field | Type | Description |
|-------|------|-------------|
| ErrorMessage | string? | Error description if this run/trial failed |
| ErrorStackTrace | string? | Stack trace if available |

These fields are nullable. For successful runs, both are null. For failed optimization trials, they contain the exception details.

### OptimizationTrialResultDto (Application Layer — Optimization)

**New fields**:

| Field | Type | Description |
|-------|------|-------------|
| ErrorMessage | string? | Error description if this trial failed |
| ErrorStackTrace | string? | Stack trace if available |

For successful trials, both are null. For failed trials, they contain the caught exception details. Metrics fields retain zero defaults.

**SQLite schema change** (migration v2 → v3):
```sql
ALTER TABLE backtest_runs ADD COLUMN error_message TEXT NULL;
ALTER TABLE backtest_runs ADD COLUMN error_stack_trace TEXT NULL;
```

### BacktestRunResponse (WebApi — Contracts)

**New fields** (mirroring BacktestRunRecord additions):

| Field | Type | Description |
|-------|------|-------------|
| ErrorMessage | string? | Error description if failed |
| ErrorStackTrace | string? | Stack trace if available |

---

## ProgressTrackingEventBusSink

File: `src/AlgoTradeForge.Application/Backtests/ProgressTrackingEventBusSink.cs`

Implements `ISink` (single method: `void Write(ReadOnlyMemory<byte> utf8Json)`). Increments a `long` counter via `Interlocked.Increment` on every `Write` call.

The handler reads this counter periodically and flushes the current value to `RunProgressCache`. This decouples the high-frequency bar processing from cache writes.

**Fields**:
- `long _processedBars` — incremented atomically on each `Write` call
- Public property `ProcessedBars` → `Interlocked.Read(ref _processedBars)`

---

## New API Response Types (WebApi — Contracts)

### BacktestSubmissionResponse

File: `src/AlgoTradeForge.WebApi/Contracts/SubmissionResponses.cs`

Returned by POST /api/backtests/ (202 Accepted).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier for polling |
| TotalBars | int | Total bars to process |
| Status | string | Initial status ("Pending") or current status if dedup hit |
| IsDedup | bool | True if this matched an existing active run |

### OptimizationSubmissionResponse

File: `src/AlgoTradeForge.WebApi/Contracts/SubmissionResponses.cs`

Returned by POST /api/optimizations/ (202 Accepted).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier for polling |
| TotalCombinations | long | Total parameter combinations |
| Status | string | Initial status ("Pending") or current status if dedup hit |
| IsDedup | bool | True if this matched an existing active run |

### BacktestStatusResponse

File: `src/AlgoTradeForge.WebApi/Contracts/StatusResponses.cs`

Returned by GET /api/backtests/{id}/status.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier |
| Status | string | Current status (Pending/Running/Completed/Failed/Cancelled) |
| ProcessedBars | int | Bars processed so far |
| TotalBars | int | Total bars to process |
| ErrorMessage | string? | Error description if failed |
| ErrorStackTrace | string? | Stack trace if failed |
| Result | BacktestRunResponse? | Full result, null until completed |

### OptimizationStatusResponse

File: `src/AlgoTradeForge.WebApi/Contracts/StatusResponses.cs`

Returned by GET /api/optimizations/{id}/status.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier |
| Status | string | Current status (Pending/Running/Completed/Failed/Cancelled) |
| CompletedCombinations | long | Trials completed so far |
| FailedCombinations | long | Trials that failed so far |
| TotalCombinations | long | Total parameter combinations |
| ErrorMessage | string? | Error description if entire optimization failed |
| ErrorStackTrace | string? | Stack trace if entire optimization failed |
| Result | OptimizationRunResponse? | Full result, null until completed |

---

## DI Registration Changes

In `src/AlgoTradeForge.WebApi/Program.cs`:
```csharp
builder.Services.AddDistributedMemoryCache();
```

In `src/AlgoTradeForge.Application/DependencyInjection.cs`:
```csharp
services.AddSingleton<RunProgressCache>();
services.AddSingleton<IRunCancellationRegistry, InMemoryRunCancellationRegistry>();
```

Handler registrations change generic params:
- `BacktestResultDto` → `BacktestSubmissionDto`
- `OptimizationResultDto` → `OptimizationSubmissionDto`

---

## Frontend Types (TypeScript)

### New Types

```typescript
interface BacktestSubmission {
  id: string;
  totalBars: number;
  status: string;
}

interface OptimizationSubmission {
  id: string;
  totalCombinations: number;
  status: string;
}

interface BacktestStatus {
  id: string;
  status: "Pending" | "Running" | "Completed" | "Failed" | "Cancelled";
  processedBars: number;
  totalBars: number;
  errorMessage?: string;
  errorStackTrace?: string;
  result?: BacktestRun;
}

interface OptimizationStatus {
  id: string;
  status: "Pending" | "Running" | "Completed" | "Failed" | "Cancelled";
  completedCombinations: number;
  failedCombinations: number;
  totalCombinations: number;
  errorMessage?: string;
  errorStackTrace?: string;
  result?: OptimizationRun;
}
```

### Modified Types

`BacktestRun` — add optional fields:
```typescript
errorMessage?: string;
errorStackTrace?: string;
```

---

## Removed Types (vs. Original Design)

The following types from the original data-model.md are **replaced** by the new architecture:

| Original Type | Replaced By | Reason |
|---------------|-------------|--------|
| `BacktestProgress` (class) | `RunProgressEntry` (record) | Unified serializable record for `IDistributedCache`; no CTS or Result field |
| `OptimizationProgress` (class) | `RunProgressEntry` (record) | Same unified record handles both run types |
| `IRunProgressStore` (interface) | `RunProgressCache` + `IRunCancellationRegistry` | Split into serializable cache wrapper + non-serializable CTS registry |
| `InMemoryRunProgressStore` (class) | `RunProgressCache` + `InMemoryRunCancellationRegistry` | Same split; cache uses `IDistributedCache`, CTS uses `ConcurrentDictionary` |
