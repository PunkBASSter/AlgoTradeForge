# Data Model: Optimization Task Queue

All types below are **in-memory only** (ephemeral). No new database tables are required. Existing DB entities (`OptimizationGroupRecord`, `OptimizationRunRecord`, `ValidationGroupRecord`, `ValidationRunRecord`) continue to handle persistence.

## New Types

### ComputeTaskType (enum)

```
Optimization   — Brute-force or genetic optimization for a single DSS
Validation     — Walk-forward validation for a single DSS
```

### ComputeTaskStatus (enum)

```
Pending        — Enqueued, waiting to execute
InProgress     — Currently executing (one at a time system-wide)
Completed      — Finished successfully (removed from queue, viewable in results)
Failed         — Execution error (removed from queue, error logged)
Cancelled      — User-cancelled or cascade-cancelled
```

### ComputeTask

In-memory task descriptor enqueued to the channel.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Unique task identifier (auto-generated) |
| JobId | Guid | Parent job ID (= OptimizationGroupId or ValidationGroupId) |
| Type | ComputeTaskType | Optimization or Validation |
| DssIndex | int | Position in the DSS subscription axis (-1 for single-run) |
| RunId | Guid | DB record ID (OptimizationRunId or ValidationRunId) |
| Status | ComputeTaskStatus | Current lifecycle state (mutable) |
| DssLabel | string | Human-readable DSS label (e.g., "BTC/binance/1h") |
| ErrorMessage | string? | Set on failure |
| EnqueuedAt | DateTimeOffset | When the task was enqueued |

### ComputeTaskQueue

Singleton service wrapping the channel and task state.

| Field | Type | Description |
|-------|------|-------------|
| Channel | Channel\<ComputeTask\> | Unbounded, single-reader channel |
| Tasks | ConcurrentDictionary\<Guid, ComputeTask\> | All tasks by ID (pending + in-progress) |
| ActiveTask | ComputeTask? | Currently executing task (null if idle) |

**Operations**:
- `Enqueue(ComputeTask task)` — adds to channel + tasks dictionary
- `EnqueueRange(IEnumerable<ComputeTask> tasks)` — batch enqueue (for multi-DSS jobs)
- `TryCancelTask(Guid taskId)` — set pending→cancelled, or signal in-progress cancellation
- `PurgePending()` — cancel all pending tasks, return count purged
- `GetSnapshot()` — returns current queue state (pending + in-progress tasks, ordered)
- `RemoveCompleted(Guid taskId)` — cleanup after task finishes

### PerDssTrialCache

Managed by the queue consumer, not exposed externally.

| Field | Type | Description |
|-------|------|-------------|
| Trials | IReadOnlyList\<BacktestRunRecord\> | Optimization trial results |
| TopTrials | BoundedTrialQueue | Thread-safe priority queue (passed to validation) |

**Lifecycle**: Created when optimization task completes → consumed by validation task → released after validation completes or is skipped.

## State Transitions

### ComputeTask Lifecycle

```
                 ┌──────────┐
   Enqueue ────► │ Pending  │
                 └────┬─────┘
                      │
              ┌───────┼──────────┐
              │       │          │
              ▼       ▼          ▼
        ┌──────────┐  User    Cascade
        │InProgress│  Cancel  Cancel
        └────┬─────┘  (opt cancelled
             │         → val cancelled)
     ┌───────┼───────┐
     │       │       │
     ▼       ▼       ▼
┌─────────┐ ┌──────┐ ┌─────────┐
│Completed│ │Failed│ │Cancelled│
└─────────┘ └──────┘ └─────────┘
```

**Valid transitions**:
- Pending → InProgress (consumer dequeues)
- Pending → Cancelled (user cancel, purge, or cascade from optimization failure)
- InProgress → Completed (execution succeeds)
- InProgress → Failed (execution error)
- InProgress → Cancelled (user cancels in-progress task)

**Terminal states**: Completed, Failed, Cancelled — task is removed from the queue snapshot.

### Cascade Cancellation (FR-008)

When an optimization task transitions to Cancelled or Failed:
1. Find all pending validation tasks with the same `JobId` and `DssIndex`
2. Set their status to Cancelled with message "Optimization was cancelled/failed"
3. Update corresponding DB records (ValidationRunRecord → status Cancelled)

## Relationship to Existing DB Entities

```
ComputeTask (in-memory)          DB Records (persistent)
─────────────────────            ─────────────────────────
JobId ─────────────────────────► OptimizationGroupRecord.Id
                                 (or ValidationGroupRecord.Id for standalone validation)

RunId (Type=Optimization) ─────► OptimizationRunRecord.Id
RunId (Type=Validation) ────────► ValidationRunRecord.Id

DssIndex ──────────────────────► OptimizationRunRecord.DssIndex
```

The queue consumer updates DB record statuses as tasks transition:
- Pending → InProgress: `OptimizationRunRecord.Status = "InProgress"`
- InProgress → Completed: `SaveOptimizationAsync()` / `SaveValidationAsync()` with final results
- → Failed/Cancelled: DB record updated with error details
