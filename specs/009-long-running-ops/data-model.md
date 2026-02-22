# Data Model: Long-Running Operations Flow

**Feature**: 009-long-running-ops
**Date**: 2026-02-22

## New Types

### RunStatus (Enum — Application Layer)

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

### BacktestProgress (Class — Application Layer)

In-memory volatile state for a running or recently completed backtest.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier (same as BacktestRunRecord.Id) |
| Status | RunStatus | Current lifecycle state |
| TotalBars | int | Total bars to process (known at submission) |
| ProcessedBars | int | Bars processed so far (updated via IEventBus) |
| ErrorMessage | string? | Error description if failed |
| ErrorStackTrace | string? | Stack trace if failed |
| Result | BacktestResultDto? | Full results, populated only on completion |
| CancellationTokenSource | CancellationTokenSource | Internal; used to cancel the background task |
| StartedAt | DateTimeOffset | When the run was submitted |

**Concurrency**: `ProcessedBars` updated via `Interlocked.Increment`. Other fields written once (on creation or terminal state).

### OptimizationProgress (Class — Application Layer)

In-memory volatile state for a running or recently completed optimization.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier (same as OptimizationRunRecord.Id) |
| Status | RunStatus | Current lifecycle state |
| TotalCombinations | long | Total parameter combinations (known at submission) |
| CompletedCombinations | long | Trials completed so far |
| FailedCombinations | long | Trials that failed |
| ErrorMessage | string? | Error description if entire optimization failed |
| ErrorStackTrace | string? | Stack trace if entire optimization failed |
| Result | OptimizationResultDto? | Full results, populated only on completion |
| CancellationTokenSource | CancellationTokenSource | Internal; used to cancel the background task |
| StartedAt | DateTimeOffset | When the run was submitted |

**Concurrency**: `CompletedCombinations` and `FailedCombinations` updated via `Interlocked.Increment`. Other fields written once.

### IRunProgressStore (Interface — Application Layer)

Thread-safe volatile store for in-progress run tracking.

```
RegisterBacktest(BacktestProgress) → void
RegisterOptimization(OptimizationProgress) → void
GetBacktest(Guid) → BacktestProgress?
GetOptimization(Guid) → OptimizationProgress?
RemoveBacktest(Guid) → bool
RemoveOptimization(Guid) → bool
```

**Implementation**: `InMemoryRunProgressStore` using two `ConcurrentDictionary<Guid, T>` instances. Registered as Singleton.

## Modified Types

### BacktestRunRecord (Application Layer — Persistence)

**New fields**:

| Field | Type | Description |
|-------|------|-------------|
| ErrorMessage | string? | Error description if this run/trial failed |
| ErrorStackTrace | string? | Stack trace if available |

These fields are nullable. For successful runs, both are null. For failed optimization trials, they contain the exception details.

**SQLite schema change**: Add two nullable TEXT columns to `backtest_runs` table:
- `error_message TEXT`
- `error_stack_trace TEXT`

### BacktestRunResponse (WebApi — Contracts)

**New fields** (mirroring BacktestRunRecord additions):

| Field | Type | Description |
|-------|------|-------------|
| ErrorMessage | string? | Error description if failed |
| ErrorStackTrace | string? | Stack trace if available |

## New API Response Types (WebApi — Contracts)

### BacktestSubmissionResponse

Returned by POST /api/backtests/ (replaces BacktestResultDto as the POST response).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier for polling |
| TotalBars | int | Total bars to process |
| Status | string | Initial status ("Pending") |

### OptimizationSubmissionResponse

Returned by POST /api/optimizations/ (replaces OptimizationResultDto as the POST response).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Run identifier for polling |
| TotalCombinations | long | Total parameter combinations |
| Status | string | Initial status ("Pending") |

### BacktestStatusResponse

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
