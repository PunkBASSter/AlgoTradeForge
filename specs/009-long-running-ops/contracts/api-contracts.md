# API Contracts: Long-Running Operations Flow

**Feature**: 009-long-running-ops
**Date**: 2026-02-22

## Modified Endpoints

### POST /api/backtests/ (Modified)

**Change**: Returns `202 Accepted` with submission response instead of `200 OK` with full results.

**Request**: `RunBacktestRequest` (unchanged)

```json
{
  "assetName": "BTCUSDT",
  "exchange": "Binance",
  "strategyName": "SmaCrossover",
  "initialCash": 10000,
  "startTime": "2024-01-01T00:00:00Z",
  "endTime": "2024-12-31T23:59:59Z",
  "commissionPerTrade": 0.001,
  "slippageTicks": 0,
  "timeFrame": "1:00:00",
  "strategyParameters": { "fastPeriod": 10, "slowPeriod": 30 }
}
```

**Response** (202 Accepted): `BacktestSubmissionResponse`

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "totalBars": 8760,
  "status": "Pending"
}
```

**Error** (400 Bad Request): Validation errors (unchanged behavior)

```json
{
  "error": "Strategy 'InvalidName' not found."
}
```

---

### POST /api/optimizations/ (Modified)

**Change**: Returns `202 Accepted` with submission response instead of `200 OK` with full results.

**Request**: `RunOptimizationRequest` (unchanged)

**Response** (202 Accepted): `OptimizationSubmissionResponse`

```json
{
  "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "totalCombinations": 2500,
  "status": "Pending"
}
```

**Error** (400 Bad Request): Validation errors (unchanged)

## New Endpoints

### GET /api/backtests/{id}/status

**Purpose**: Poll for backtest progress and results.

**Response** (200 OK): `BacktestStatusResponse`

While running:
```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Running",
  "processedBars": 3200,
  "totalBars": 8760,
  "errorMessage": null,
  "errorStackTrace": null,
  "result": null
}
```

When completed:
```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Completed",
  "processedBars": 8760,
  "totalBars": 8760,
  "errorMessage": null,
  "errorStackTrace": null,
  "result": {
    "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "strategyName": "SmaCrossover",
    "strategyVersion": "1.0.0",
    "parameters": { "fastPeriod": 10, "slowPeriod": 30 },
    "assetName": "BTCUSDT",
    "exchange": "Binance",
    "timeFrame": "1:00:00",
    "initialCash": 10000,
    "commission": 0.001,
    "slippageTicks": 0,
    "startedAt": "2026-02-22T10:00:00Z",
    "completedAt": "2026-02-22T10:00:45Z",
    "dataStart": "2024-01-01T00:00:00Z",
    "dataEnd": "2024-12-31T23:59:59Z",
    "durationMs": 45000,
    "totalBars": 8760,
    "metrics": {
      "sharpeRatio": 1.85,
      "sortinoRatio": 2.10,
      "netProfit": 2500.00,
      "maxDrawdownPct": -12.5,
      "winRatePct": 58.3,
      "profitFactor": 1.65,
      "totalTrades": 120
    },
    "hasCandleData": true,
    "runMode": "Standalone",
    "errorMessage": null,
    "errorStackTrace": null
  }
}
```

When failed:
```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Failed",
  "processedBars": 3200,
  "totalBars": 8760,
  "errorMessage": "Division by zero in SortinoRatio calculation",
  "errorStackTrace": "at AlgoTradeForge.Domain.Reporting...",
  "result": null
}
```

**Error** (404 Not Found): Run ID not found in progress store or persistence layer.

```json
{
  "error": "Run 'a1b2c3d4-...' not found."
}
```

---

### GET /api/optimizations/{id}/status

**Purpose**: Poll for optimization progress and results.

**Response** (200 OK): `OptimizationStatusResponse`

While running:
```json
{
  "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "status": "Running",
  "completedCombinations": 850,
  "failedCombinations": 3,
  "totalCombinations": 2500,
  "errorMessage": null,
  "errorStackTrace": null,
  "result": null
}
```

When completed:
```json
{
  "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "status": "Completed",
  "completedCombinations": 2497,
  "failedCombinations": 3,
  "totalCombinations": 2500,
  "errorMessage": null,
  "errorStackTrace": null,
  "result": {
    "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
    "strategyName": "SmaCrossover",
    "strategyVersion": "1.0.0",
    "startedAt": "2026-02-22T10:00:00Z",
    "completedAt": "2026-02-22T10:45:00Z",
    "durationMs": 2700000,
    "totalCombinations": 2500,
    "sortBy": "SharpeRatio",
    "dataStart": "2024-01-01T00:00:00Z",
    "dataEnd": "2024-12-31T23:59:59Z",
    "initialCash": 10000,
    "commission": 0.001,
    "slippageTicks": 0,
    "maxParallelism": 8,
    "assetName": "BTCUSDT",
    "exchange": "Binance",
    "timeFrame": "1:00:00",
    "trials": [
      {
        "id": "...",
        "parameters": { "fastPeriod": 12, "slowPeriod": 26 },
        "metrics": { "sharpeRatio": 2.1, "netProfit": 3500 },
        "errorMessage": null,
        "errorStackTrace": null
      },
      {
        "id": "...",
        "parameters": { "fastPeriod": 5, "slowPeriod": 50 },
        "metrics": { "sharpeRatio": 0, "netProfit": 0 },
        "errorMessage": "Division by zero",
        "errorStackTrace": "at ..."
      }
    ]
  }
}
```

**Error** (404 Not Found): Same as backtest.

---

### POST /api/backtests/{id}/cancel

**Purpose**: Cancel an in-progress backtest.

**Response** (200 OK): Cancellation accepted.

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Cancelled"
}
```

**Error** (404 Not Found): Run not found.
**Error** (409 Conflict): Run already in terminal state.

```json
{
  "error": "Run 'a1b2c3d4-...' is already Completed and cannot be cancelled."
}
```

---

### POST /api/optimizations/{id}/cancel

**Purpose**: Cancel an in-progress optimization.

Same response contract as backtest cancel.

## Unchanged Endpoints

The following existing endpoints remain unchanged and continue to serve persisted (completed) data:

- `GET /api/backtests/` — List completed backtests (paged)
- `GET /api/backtests/{id}` — Get completed backtest by ID
- `GET /api/backtests/{id}/equity` — Get equity curve
- `GET /api/optimizations/` — List completed optimizations (paged)
- `GET /api/optimizations/{id}` — Get completed optimization with trials

## Endpoint Summary

| Method | Path | Status | Change |
|--------|------|--------|--------|
| POST | /api/backtests/ | 202 | Modified (was 200) |
| POST | /api/optimizations/ | 202 | Modified (was 200) |
| GET | /api/backtests/{id}/status | 200 | New |
| GET | /api/optimizations/{id}/status | 200 | New |
| POST | /api/backtests/{id}/cancel | 200 | New |
| POST | /api/optimizations/{id}/cancel | 200 | New |
| GET | /api/backtests/ | 200 | Unchanged |
| GET | /api/backtests/{id} | 200 | Unchanged (+ error fields) |
| GET | /api/backtests/{id}/equity | 200 | Unchanged |
| GET | /api/optimizations/ | 200 | Unchanged |
| GET | /api/optimizations/{id} | 200 | Unchanged (+ error fields) |
