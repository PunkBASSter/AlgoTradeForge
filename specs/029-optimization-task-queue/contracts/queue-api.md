# API Contracts: Task Queue Endpoints

## Queue Status

### GET /api/queue

Returns current queue snapshot (pending + in-progress tasks only).

**Response** `200 OK`:
```json
{
  "activeTasks": [
    {
      "id": "guid",
      "jobId": "guid",
      "type": "Optimization",
      "dssIndex": 0,
      "dssLabel": "BTC/binance/1h",
      "runId": "guid",
      "status": "InProgress",
      "enqueuedAt": "2026-04-14T10:00:00Z",
      "progress": {
        "processed": 1500,
        "total": 3000
      }
    },
    {
      "id": "guid",
      "jobId": "guid",
      "type": "Validation",
      "dssIndex": 0,
      "dssLabel": "BTC/binance/1h",
      "runId": "guid",
      "status": "Pending",
      "enqueuedAt": "2026-04-14T10:00:00Z",
      "progress": null
    }
  ],
  "pendingCount": 3,
  "inProgressTask": "guid or null"
}
```

**Notes**:
- Tasks are ordered: in-progress first, then pending in enqueue order
- `progress` is non-null only for in-progress tasks
- For optimization: `processed`/`total` = combination counts
- For validation: `processed`/`total` = current stage / total stages (8)

## Cancel Task

### POST /api/queue/{taskId}/cancel

Cancel a single pending or in-progress task. Cascade-cancels related validation tasks if applicable.

**Response** `200 OK`:
```json
{
  "taskId": "guid",
  "status": "Cancelled",
  "cascadeCancelled": ["guid", "guid"]
}
```

**Response** `404 Not Found`: Task not in queue (already completed or doesn't exist)

**Response** `409 Conflict`: Task already in terminal state

## Purge Pending

### POST /api/queue/purge

Remove all pending tasks from the queue. Does not affect the currently in-progress task.

**Response** `200 OK`:
```json
{
  "purgedCount": 5,
  "purgedTaskIds": ["guid", "guid", "guid", "guid", "guid"]
}
```

## Modified Endpoints

### POST /api/optimizations/ (existing, modified)

**Added request fields**:
```json
{
  "...existing fields...",
  "validate": false,
  "thresholdProfileName": "Crypto-Standard",
  "maxThreads": 0
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| validate | bool | false | If true, enqueue validation tasks after optimization per DSS |
| thresholdProfileName | string | "Crypto-Standard" | Validation profile (used only when validate=true) |
| maxThreads | int | 0 | 0=CPU count, positive=capped at CPU count |

**Response** `202 Accepted` (unchanged structure, adds `enqueuedTasks`):
```json
{
  "id": "guid",
  "totalCombinationsPerRun": 3000,
  "enqueuedTasks": 4
}
```

### POST /api/validations/groups (existing, modified)

Now enqueues validation tasks through the queue instead of launching directly.

**Request**: Unchanged
**Response** `202 Accepted` (adds `enqueuedTasks`):
```json
{
  "id": "guid",
  "totalRuns": 2,
  "enqueuedTasks": 2
}
```

## Frontend Type Additions

### TaskQueueItem (TypeScript)

```typescript
interface TaskQueueItem {
  id: string;
  jobId: string;
  type: "Optimization" | "Validation";
  dssIndex: number;
  dssLabel: string;
  runId: string;
  status: "Pending" | "InProgress";
  enqueuedAt: string;
  progress: { processed: number; total: number } | null;
}

interface TaskQueueSnapshot {
  activeTasks: TaskQueueItem[];
  pendingCount: number;
  inProgressTask: string | null;
}

interface CancelTaskResponse {
  taskId: string;
  status: string;
  cascadeCancelled: string[];
}

interface PurgeResponse {
  purgedCount: number;
  purgedTaskIds: string[];
}
```
