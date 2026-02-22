# API Endpoints Contract

**Feature**: 008-trading-frontend
**Date**: 2026-02-21

## Strategies

### GET /api/strategies

Returns distinct strategy names from run history.

**Response**: `200 OK` — `string[]`

```json
["SmaCrossover", "BollingerBreakout", "MeanReversion"]
```

---

## Backtests

### POST /api/backtests

Run a backtest with the specified parameters.

**Request Body**:
```json
{
  "assetName": "BTCUSDT",
  "exchange": "Binance",
  "strategyName": "SmaCrossover",
  "initialCash": 10000.0,
  "startTime": "2025-01-01T00:00:00Z",
  "endTime": "2025-12-31T23:59:59Z",
  "commissionPerTrade": 0.001,
  "slippageTicks": 2,
  "timeFrame": "00:15:00",
  "strategyParameters": { "fastPeriod": 10, "slowPeriod": 30 }
}
```

**Response**: `200 OK` — `BacktestResultDto`
**Error**: `400 Bad Request` — `{ "error": "..." }`

### GET /api/backtests

List backtest runs with optional filters.

**Query Parameters**:

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| strategyName | string | No | — | Filter by strategy |
| assetName | string | No | — | Filter by asset |
| exchange | string | No | — | Filter by exchange |
| timeFrame | string | No | — | Filter by timeframe |
| standaloneOnly | bool | No | — | Exclude optimization trials |
| from | DateTimeOffset | No | — | Filter by completed_at >= |
| to | DateTimeOffset | No | — | Filter by completed_at <= |
| limit | int | No | 50 | Page size |
| offset | int | No | 0 | Page offset |

**Response**: `200 OK` — `PagedResponse<BacktestRun>`

Results sorted by `completed_at DESC` (most recent first).

### GET /api/backtests/{id}

Get a backtest result by ID.

**Response**: `200 OK` — `BacktestRun`
**Error**: `404 Not Found`

### GET /api/backtests/{id}/equity

Get the equity curve for a backtest run.

**Response**: `200 OK` — `EquityPoint[]`
**Error**: `404 Not Found`

```json
[
  { "timestampMs": 1706745600000, "value": 10000.0 },
  { "timestampMs": 1706832000000, "value": 10250.5 }
]
```

### GET /api/backtests/{id}/events (Backend Prerequisite — Not Yet Implemented)

Get parsed event data (candles, indicators, trades) for a backtest run.

**Response**: `200 OK` — `EventsData`
**Error**: `404 Not Found`

Only available when `hasCandleData` is true on the backtest run.

```json
{
  "candles": [
    { "time": 1706745600, "open": 42150.5, "high": 42200.0, "low": 42100.0, "close": 42180.0, "volume": 1234.5 }
  ],
  "indicators": {
    "SMA": { "measure": "price", "points": [{ "time": 1706745600, "value": 42100.0 }] }
  },
  "trades": [
    {
      "entryTime": 1706745600, "entryPrice": 42150.5, "exitTime": 1706832000, "exitPrice": 42500.0,
      "side": "buy", "quantity": 0.5, "pnl": 174.75, "commission": 2.0,
      "takeProfitPrice": 43000.0, "stopLossPrice": 41500.0
    }
  ]
}
```

---

## Optimizations

### POST /api/optimizations

Run a brute-force parameter optimization.

**Request Body**:
```json
{
  "strategyName": "SmaCrossover",
  "optimizationAxes": {
    "fastPeriod": { "min": 5, "max": 20, "step": 5 },
    "slowPeriod": { "min": 20, "max": 50, "step": 10 }
  },
  "dataSubscriptions": [
    { "asset": "BTCUSDT", "exchange": "Binance", "timeFrame": "00:15:00" }
  ],
  "initialCash": 10000.0,
  "startTime": "2025-01-01T00:00:00Z",
  "endTime": "2025-12-31T23:59:59Z",
  "commissionPerTrade": 0.001,
  "slippageTicks": 2,
  "maxDegreeOfParallelism": -1,
  "maxCombinations": 100000,
  "sortBy": "sortinoRatio"
}
```

**Response**: `200 OK` — `OptimizationResultDto`
**Error**: `400 Bad Request`

### GET /api/optimizations

List optimization runs with optional filters.

**Query Parameters**: Same as backtests (minus `standaloneOnly`).

**Response**: `200 OK` — `PagedResponse<OptimizationRun>`

Results sorted by `completed_at DESC`.

### GET /api/optimizations/{id}

Get an optimization run with all trials.

**Response**: `200 OK` — `OptimizationRun` (includes `trials` array)
**Error**: `404 Not Found`

---

## Debug Sessions

All debug endpoints are **localhost-only** (403 Forbidden for non-loopback).

### POST /api/debug-sessions

Start a debug backtest session (paused).

**Request Body**: Same shape as `RunBacktestRequest` (via `StartDebugSessionRequest`).

**Response**: `201 Created` — `DebugSession`

```json
{
  "sessionId": "a1b2c3d4-...",
  "assetName": "BTCUSDT",
  "strategyName": "SmaCrossover",
  "createdAt": "2026-02-21T10:00:00Z"
}
```

**Error**: `400 Bad Request`

### GET /api/debug-sessions/{id}

Get debug session status.

**Response**: `200 OK` — `DebugSessionStatus`
**Error**: `404 Not Found`

### DELETE /api/debug-sessions/{id}

Terminate and clean up a debug session.

**Response**: `204 No Content`
**Error**: `404 Not Found`

---

## Debug WebSocket

### WS /api/debug-sessions/{id}/ws

Bidirectional WebSocket for real-time event streaming and command control.

**Localhost-only**. Only one client connection per session (409 Conflict for second).

**Client → Server** (JSON):
```json
{ "command": "next_bar" }
{ "command": "run_to_timestamp", "timestampMs": 1706745600000 }
{ "command": "run_to_sequence", "sequenceNumber": 500 }
{ "command": "continue" }
{ "command": "pause" }
{ "command": "set_export", "mutations": true }
```

**Server → Client** (mixed formats):
1. **Raw JSONL events** (one per line, no `type` wrapper):
   ```json
   {"ts":"2025-06-01T12:00:00Z","sq":1,"_t":"bar","src":"engine","d":{"assetName":"BTCUSDT","timeFrame":"1h","open":42150,"high":42200,"low":42100,"close":42180,"volume":1234}}
   ```
2. **Snapshot** (after each command completes):
   ```json
   {"type":"snapshot","sessionActive":true,"sequenceNumber":42,"timestampMs":1706745600000,"subscriptionIndex":0,"isExportableSubscription":true,"fillsThisBar":0,"portfolioEquity":1000000000000}
   ```
3. **Error**:
   ```json
   {"type":"error","message":"Session has ended."}
   ```
4. **Export acknowledgement**:
   ```json
   {"type":"set_export_ack","mutations":true}
   ```

**Message discrimination**: If the parsed JSON has a `type` field, it's a control message (snapshot/error/ack). Otherwise it's a raw backtest event with `_t` field.
