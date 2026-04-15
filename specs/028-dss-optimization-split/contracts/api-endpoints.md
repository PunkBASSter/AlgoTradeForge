# API Contracts: Per-DSS Optimization Split

**Branch**: `028-dss-optimization-split` | **Date**: 2026-04-13

## Overview

Changes to the REST API at `/api/`. Existing endpoints are modified to support the group concept. Group-specific sub-routes are nested under `/api/optimizations/groups/` and `/api/validations/groups/`. No new top-level API paths.

---

## Modified Endpoints

### POST /api/optimizations — Run Brute-Force Optimization

**Change**: Now creates an OptimizationGroup with N child runs instead of a single run.

**Request** (modified):
```json
{
  "strategyName": "ZigZagBreakout",
  "backtestSettings": {
    "initialCash": 10000,
    "startTime": "2024-01-01T00:00:00Z",
    "endTime": "2025-01-01T00:00:00Z",
    "commissionPerTrade": 0.001,
    "slippageTicks": 1
  },
  "optimizationSettings": {
    "maxTrialsToKeep": 100,
    "maxDegreeOfParallelism": 4,
    "maxCombinations": 500000,
    "minTradeCount": 30,
    "fitnessWeights": { "sharpe": 0.5, "sortino": 0.2, "profitFactor": 0.15, "annualizedReturn": 0.15 }
  },
  "optimizationAxes": {
    "Period": { "type": "range", "min": 10, "max": 50, "step": 5 },
    "Threshold": { "type": "range", "min": 0.5, "max": 3.0, "step": 0.25 }
  },
  "subscriptionAxis": [
    [{ "assetName": "BTC", "exchange": "binance", "timeFrame": "1h" }],
    [{ "assetName": "ETH", "exchange": "binance", "timeFrame": "1h" }],
    [{ "assetName": "SOL", "exchange": "binance", "timeFrame": "1h" }]
  ]
}
```

**Response** (modified — 202 Accepted):
```json
{
  "groupId": "a1b2c3d4-...",
  "runs": [
    { "id": "run-1-guid", "dss": [{ "assetName": "BTC", "exchange": "binance", "timeFrame": "1h" }], "totalCombinations": 45 },
    { "id": "run-2-guid", "dss": [{ "assetName": "ETH", "exchange": "binance", "timeFrame": "1h" }], "totalCombinations": 45 },
    { "id": "run-3-guid", "dss": [{ "assetName": "SOL", "exchange": "binance", "timeFrame": "1h" }], "totalCombinations": 45 }
  ],
  "totalCombinationsPerRun": 45
}
```

### POST /api/optimizations/genetic — Run Genetic Optimization

Same structure changes as brute-force. `subscriptionAxis` becomes the DSS list for group creation. Same response shape.

### POST /api/optimizations/evaluate — Evaluate Combination Count

**Change**: Returns per-run combination count (no longer multiplied by DSS count).

**Response** (modified):
```json
{
  "totalCombinations": 45,
  "uniqueCombinations": 42,
  "dssCount": 3,
  "effectiveDimensions": 2,
  "resolvedGeneticConfig": null
}
```

### GET /api/optimizations — List Optimizations

**Change**: Now returns optimization groups instead of flat individual runs. Same path, new response shape.

**Query params**: `strategyName?`, `from?`, `to?`, `limit?`, `offset?`

**Response** (200):
```json
{
  "items": [
    {
      "id": "a1b2c3d4-...",
      "strategyName": "ZigZagBreakout",
      "strategyVersion": "1",
      "optimizationMethod": "BruteForce",
      "startedAt": "2026-04-13T10:00:00Z",
      "completedAt": "2026-04-13T10:15:00Z",
      "totalRuns": 3,
      "completedRuns": 3,
      "failedRuns": 0,
      "status": "Completed",
      "subscriptions": [
        [{ "assetName": "BTC", "exchange": "binance", "timeFrame": "1h" }],
        [{ "assetName": "ETH", "exchange": "binance", "timeFrame": "1h" }],
        [{ "assetName": "SOL", "exchange": "binance", "timeFrame": "1h" }]
      ]
    }
  ],
  "total": 15
}
```

---

## Group Sub-Routes (under /api/optimizations/groups/)

### GET /api/optimizations/groups/{groupId} — Get Optimization Group Detail

**Response** (200):
```json
{
  "id": "a1b2c3d4-...",
  "strategyName": "ZigZagBreakout",
  "strategyVersion": "1",
  "optimizationMethod": "BruteForce",
  "startedAt": "2026-04-13T10:00:00Z",
  "completedAt": "2026-04-13T10:15:00Z",
  "status": "Completed",
  "totalRuns": 3,
  "maxParallelism": 4,
  "inputJson": "{ ... }",
  "runs": [
    {
      "id": "run-1-guid",
      "dss": [{ "assetName": "BTC", "exchange": "binance", "timeFrame": "1h" }],
      "status": "Completed",
      "totalCombinations": 45,
      "keptTrials": 38,
      "filteredTrials": 5,
      "failedTrials": 2,
      "durationMs": 12345,
      "startedAt": "2026-04-13T10:00:00Z",
      "completedAt": "2026-04-13T10:05:00Z"
    },
    { "...": "..." }
  ]
}
```

### GET /api/optimizations/groups/{groupId}/trials — Cross-DSS Trials

Combined trials from all runs in the group. Sorted using denormalized metric columns.

**Query params**: `limit?` (default 1000), `offset?` (default 0), `sortBy?` (default "FitnessScore")

**Response** (200):
```json
{
  "items": [
    {
      "id": "trial-guid",
      "runId": "run-1-guid",
      "dss": [{ "assetName": "BTC", "exchange": "binance", "timeFrame": "1h" }],
      "fitnessScore": 2.45,
      "sharpeRatio": 1.82,
      "sortinoRatio": 2.31,
      "profitFactor": 1.95,
      "maxDrawdownPct": 12.5,
      "winRatePct": 58.3,
      "totalTrades": 142,
      "netProfit": 4523.50,
      "params": "Period:20, Threshold:1.5"
    }
  ],
  "total": 114
}
```

### GET /api/optimizations/groups/{groupId}/status — Group Status

**Response** (200):
```json
{
  "id": "a1b2c3d4-...",
  "status": "InProgress",
  "runs": [
    { "id": "run-1-guid", "status": "Completed", "processed": 45, "total": 45 },
    { "id": "run-2-guid", "status": "InProgress", "processed": 22, "total": 45 },
    { "id": "run-3-guid", "status": "InProgress", "processed": 18, "total": 45 }
  ]
}
```

### POST /api/optimizations/groups/{groupId}/cancel — Cancel Group

Cancels all in-progress runs. Completed runs are preserved.

**Response** (204 No Content)

### DELETE /api/optimizations/groups/{groupId} — Delete Group

Cascade-deletes all child runs, trials, and failed trial records.

**Response** (204 No Content)

---

## Validation Endpoints

### POST /api/validations — Run Group Validation

**Change**: Now takes an `optimizationGroupId` instead of `optimizationRunId`. Creates a validation group with per-DSS validation runs.

**Request** (modified):
```json
{
  "optimizationGroupId": "a1b2c3d4-...",
  "thresholdProfileName": "Crypto-Standard",
  "maxTrialsToValidate": 100
}
```

The `maxTrialsToValidate` field is optional (default 100). It caps how many top trials by fitness are validated per DSS, preventing excessive runtime from the 8-stage validation pipeline.

**Response** (202 Accepted):
```json
{
  "groupId": "val-group-guid",
  "runs": [
    { "id": "val-run-1", "optimizationRunId": "run-1-guid", "candidateCount": 38 },
    { "id": "val-run-2", "optimizationRunId": "run-2-guid", "candidateCount": 35 },
    { "id": "val-run-3", "optimizationRunId": "run-3-guid", "candidateCount": 40 }
  ]
}
```

### GET /api/validations — List Validations

**Change**: Now returns validation groups instead of flat individual runs. Same path, new response shape.

**Query params**: `strategyName?`, `from?`, `to?`, `limit?`, `offset?`

### GET /api/validations/groups/{groupId} — Get Validation Group Detail

**Response** (200):
```json
{
  "id": "val-group-guid",
  "optimizationGroupId": "a1b2c3d4-...",
  "strategyName": "ZigZagBreakout",
  "thresholdProfileName": "Crypto-Standard",
  "status": "Completed",
  "startedAt": "2026-04-13T11:00:00Z",
  "completedAt": "2026-04-13T11:20:00Z",
  "totalRuns": 3,
  "runs": [
    {
      "id": "val-run-1",
      "optimizationRunId": "run-1-guid",
      "dss": [{ "assetName": "BTC", "exchange": "binance", "timeFrame": "1h" }],
      "status": "Completed",
      "candidatesIn": 38,
      "candidatesOut": 12,
      "compositeScore": 0.72,
      "verdict": "Green"
    }
  ]
}
```

### GET /api/validations/groups/{groupId}/trials — Cross-DSS Validation Trials

Same shape as optimization cross-DSS trials, but includes validation-specific fields (composite score, verdict, stage results summary).

### GET /api/validations/groups/{groupId}/status — Validation Group Status

Same pattern as optimization group status.

### POST /api/validations/groups/{groupId}/cancel — Cancel Validation Group

Same pattern as optimization group cancel.

### DELETE /api/validations/groups/{groupId} — Delete Validation Group

Cascade-deletes all child validation runs and stage results.

---

## Existing Per-Run Endpoints (Unchanged)

These continue to work for individual runs within a group:

| Endpoint | Change |
|----------|--------|
| GET /api/optimizations/{id} | Returns individual run detail (unchanged) |
| GET /api/optimizations/{id}/trials | Add `params` field to each trial in response |
| GET /api/optimizations/{id}/status | Returns individual run progress (unchanged) |
| POST /api/optimizations/{id}/cancel | Cancels individual run (unchanged) |
| DELETE /api/optimizations/{id} | Deletes individual run (unchanged) |
| GET /api/validations/{id} | Returns individual validation run (unchanged) |

### Modified Trial Response Shape

All trial/backtest endpoints add a `params` field:

```json
{
  "id": "trial-guid",
  "...": "existing fields",
  "params": "Period:20, Threshold:1.5, Mode:FollowTrend"
}
```
