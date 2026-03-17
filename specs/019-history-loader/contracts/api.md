# API Contracts: History Loader

**Branch**: `019-history-loader` | **Date**: 2026-03-13
**Base URL**: `http://localhost:{port}/api/v1`

---

## Health Check

### `GET /health`

Standard ASP.NET Core health check.

**Response** `200 OK`:
```json
{
  "status": "Healthy"
}
```

---

## Collection Status

### `GET /api/v1/status`

Returns collection status for all configured symbols.

**Response** `200 OK`:
```json
{
  "symbols": [
    {
      "symbol": "BTCUSDT",
      "type": "perpetual",
      "exchange": "binance",
      "feedCount": 10,
      "feeds": [
        {
          "name": "candles",
          "interval": "1m",
          "lastTimestamp": 1710288000000,
          "gapCount": 0,
          "health": "Healthy"
        },
        {
          "name": "open-interest",
          "interval": "5m",
          "lastTimestamp": 1710287700000,
          "gapCount": 1,
          "health": "Degraded"
        }
      ]
    }
  ]
}
```

### `GET /api/v1/status/{symbol}`

Returns detailed status for a specific symbol, including full gap information.

**Path Parameters**:
- `symbol` (string, required): Symbol directory name (e.g., `BTCUSDT_fut`, `BTCUSDT`)

**Response** `200 OK`:
```json
{
  "symbol": "BTCUSDT",
  "type": "perpetual",
  "exchange": "binance",
  "feeds": [
    {
      "name": "open-interest",
      "interval": "5m",
      "firstTimestamp": 1707350400000,
      "lastTimestamp": 1710287700000,
      "lastRunUtc": "2026-03-13T10:15:00Z",
      "recordCount": 82944,
      "health": "Degraded",
      "gaps": [
        {
          "fromMs": 1709856000000,
          "toMs": 1709942400000
        }
      ]
    }
  ]
}
```

**Response** `404 Not Found`:
```json
{
  "error": "Symbol not found",
  "symbol": "INVALID"
}
```

---

## On-Demand Backfill

### `POST /api/v1/backfill`

Triggers an on-demand backfill for a specific symbol. Runs asynchronously — returns immediately with confirmation.

**Request Body**:
```json
{
  "symbol": "BTCUSDT_fut",
  "feeds": ["candles", "funding-rate"],
  "fromDate": "2019-09-01"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `symbol` | string | Yes | Symbol directory name |
| `feeds` | string[] | No | Feed names to backfill. Null = all configured feeds |
| `fromDate` | string (date) | No | Start date. Null = use asset's `HistoryStart` config |

**Response** `202 Accepted`:
```json
{
  "symbol": "BTCUSDT_fut",
  "feedsQueued": ["candles", "funding-rate"],
  "message": "Backfill queued for 2 feeds"
}
```

**Response** `400 Bad Request`:
```json
{
  "error": "Symbol not configured",
  "symbol": "INVALID"
}
```

**Response** `409 Conflict`:
```json
{
  "error": "Backfill already running for this symbol",
  "symbol": "BTCUSDT_fut"
}
```

---

## Endpoint Summary

| Method | Path | Description | Status Codes |
|--------|------|-------------|-------------|
| GET | `/health` | Health check | 200 |
| GET | `/api/v1/status` | All symbols status | 200 |
| GET | `/api/v1/status/{symbol}` | Single symbol detail | 200, 404 |
| POST | `/api/v1/backfill` | Trigger backfill | 202, 400, 409 |
