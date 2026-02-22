# Research: Trading Frontend

**Feature**: 008-trading-frontend
**Date**: 2026-02-21

## R1: JSON Editor Library

**Decision**: CodeMirror 6 with `@codemirror/lang-json`

**Rationale**: CodeMirror 6 is modular, tree-shakeable, has excellent dark theme support, built-in JSON syntax validation, and works well as a React Client Component. It's lighter than Monaco Editor (VS Code's editor) and doesn't require web workers. For a JSON-only use case (debug session config, run-new panel), CodeMirror is the right fit.

**Alternatives considered**:
- **Monaco Editor**: Full IDE capabilities, but heavy (~2MB+), requires web worker setup, overkill for JSON editing
- **Plain textarea**: Too basic — no syntax highlighting, no validation feedback, poor UX for nested JSON structures

## R2: WebSocket Client Management

**Decision**: Custom React hook (`useDebugWebSocket`) wrapping the native `WebSocket` API

**Rationale**: Single-user localhost app with one WebSocket connection at a time. No need for reconnection libraries (socket.io, reconnecting-websocket). The custom hook manages: connection lifecycle tied to session ID, message parsing (JSONL events vs typed JSON messages), command sending, and cleanup on unmount. Zustand store holds accumulated debug state (candles, indicators, trades, metrics).

**Alternatives considered**:
- **socket.io-client**: Adds unnecessary overhead and protocol translation; backend uses raw WebSocket, not socket.io
- **TanStack Query WebSocket adapter**: TanStack Query is designed for request/response, not streaming; would require awkward workarounds

## R3: Mock Mode Implementation Pattern

**Decision**: Service layer abstraction with environment-variable-toggled mock implementations

**Rationale**: Define TypeScript interfaces for each data source (backtests API, optimizations API, strategies API, debug WebSocket). Real implementations call the backend; mock implementations return static fixtures and replay JSONL sequences. Toggle via `NEXT_PUBLIC_MOCK_MODE=true` environment variable. TanStack Query `queryFn` functions call the service interface, making the swap transparent.

**Pattern**:
```
lib/services/api-client.ts        → Real HTTP client (fetch-based)
lib/services/mock-client.ts       → Mock data provider
lib/services/index.ts             → Exports active implementation based on env var
lib/services/mock-data/            → Static JSON fixtures
```

**Alternatives considered**:
- **MSW (Mock Service Worker)**: Good for testing but adds runtime complexity; for dev-time mock mode, direct service swap is simpler
- **Next.js API routes as mock server**: Unnecessary indirection when the mock can live entirely client-side

## R4: Events Endpoint Design (Backend Prerequisite)

**Decision**: Backend adds `GET /api/backtests/{id}/events` returning pre-parsed structured JSON

**Rationale**: The `events.jsonl` file contains raw JSONL with Int64 domain values. Having the backend parse and convert to decimal values (using the asset's tick size) avoids the frontend needing tick-size knowledge. The response groups events by type for efficient rendering.

**Proposed response structure**:
```json
{
  "candles": [
    { "time": 1706745600, "open": 42150.5, "high": 42200.0, "low": 42100.0, "close": 42180.0, "volume": 1234.5 }
  ],
  "indicators": {
    "SMA": { "measure": "price", "points": [{ "time": 1706745600, "value": 42100.0 }] },
    "RSI": { "measure": "percent", "points": [{ "time": 1706745600, "value": 65.2 }] }
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

**Key decisions**:
- Times as Unix timestamps (seconds) — matches TradingView Lightweight Charts `UTCTimestamp` format
- Prices as decimals (backend converts from Int64 domain values)
- Indicators grouped by name with measure type for chart overlay placement
- Trades reconstructed from `ord.fill`/`pos` event sequences by the backend

## R5: Int64 and Decimal Handling in Frontend

**Decision**: Int64 values from WebSocket events handled as JavaScript `number`; backend events endpoint returns pre-converted decimals

**Rationale**: For the debug WebSocket stream, raw events contain Int64 values (e.g., `bar.open`, `ord.fill.price`). Maximum expected values for crypto prices (e.g., BTC at ~$100,000 with 8 decimal places = 10,000,000,000,000) are within `Number.MAX_SAFE_INTEGER` (9,007,199,254,740,991). Standard `JSON.parse` handles these safely. The debug screen will need tick-size context from the `run.start` event to display human-readable prices, or display raw values with a note.

For the report screen, the backend events endpoint (R4) returns pre-converted decimal values, so no conversion needed.

**Alternatives considered**:
- **BigInt for all Int64 fields**: Adds complexity to chart data transformations; unnecessary given value ranges
- **String parsing**: JSON number fields already parse to `number` correctly within safe integer range

## R6: TradingView Lightweight Charts with Next.js 16

**Decision**: Wrap in Client Components with `"use client"` directive, lazy-loaded via `next/dynamic` with `ssr: false`

**Rationale**: TradingView Lightweight Charts requires DOM access and cannot be server-rendered. Using `next/dynamic` with `ssr: false` prevents hydration errors. Chart components must handle cleanup (`chart.remove()`) in `useEffect` return. Data updates use `ISeriesApi.update()` for incremental adds (debug mode) and `ISeriesApi.setData()` for bulk loads (report mode).

**Performance considerations**:
- For debug auto-play with rapid events, batch updates using `requestAnimationFrame` to avoid layout thrashing
- For report charts with 200+ candles, `setData()` once rather than incremental `update()` calls
- Use `chart.timeScale().fitContent()` after initial data load for optimal zoom
