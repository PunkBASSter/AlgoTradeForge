# Data Model: Trading Frontend

**Feature**: 008-trading-frontend
**Date**: 2026-02-21

## API Response Types

### Strategy

```typescript
// GET /api/strategies → string[]
type StrategyList = string[];
```

### Backtest Run

```typescript
// GET /api/backtests → PagedResponse<BacktestRun>
// GET /api/backtests/{id} → BacktestRun
interface BacktestRun {
  id: string;                    // GUID
  strategyName: string;
  strategyVersion: string;
  parameters: Record<string, unknown>;
  assetName: string;
  exchange: string;
  timeFrame: string;             // e.g., "00:15:00"
  initialCash: number;           // decimal
  commission: number;            // decimal
  slippageTicks: number;
  startedAt: string;             // ISO 8601
  completedAt: string;           // ISO 8601
  dataStart: string;             // ISO 8601
  dataEnd: string;               // ISO 8601
  durationMs: number;
  totalBars: number;
  metrics: Record<string, number>;  // dynamic metric dictionary
  hasCandleData: boolean;
  runMode: string;               // "Backtest"
  optimizationRunId?: string;    // GUID, present if trial of optimization
}
```

### Optimization Run

```typescript
// GET /api/optimizations → PagedResponse<OptimizationRun>
// GET /api/optimizations/{id} → OptimizationRun
interface OptimizationRun {
  id: string;                    // GUID
  strategyName: string;
  strategyVersion: string;
  startedAt: string;             // ISO 8601
  completedAt: string;           // ISO 8601
  durationMs: number;
  totalCombinations: number;
  sortBy: string;
  dataStart: string;             // ISO 8601
  dataEnd: string;               // ISO 8601
  initialCash: number;           // decimal
  commission: number;            // decimal
  slippageTicks: number;
  maxParallelism: number;
  assetName: string;
  exchange: string;
  timeFrame: string;
  trials: BacktestRun[];
}
```

### Paged Response

```typescript
// Generic paged wrapper for list endpoints
interface PagedResponse<T> {
  items: T[];
  totalCount: number;
  limit: number;
  offset: number;
  hasMore: boolean;
}
```

### Equity Point

```typescript
// GET /api/backtests/{id}/equity → EquityPoint[]
interface EquityPoint {
  timestampMs: number;           // epoch milliseconds
  value: number;                 // decimal portfolio value
}
```

### Events Data (Backend Prerequisite Endpoint)

```typescript
// GET /api/backtests/{id}/events → EventsData
// Only available when hasCandleData is true
interface EventsData {
  candles: CandleData[];
  indicators: Record<string, IndicatorSeries>;
  trades: TradeData[];
}

interface CandleData {
  time: number;                  // Unix timestamp (seconds) for TradingView
  open: number;                  // decimal
  high: number;                  // decimal
  low: number;                   // decimal
  close: number;                 // decimal
  volume: number;                // decimal
}

interface IndicatorSeries {
  measure: "price" | "percent" | "minusOnePlusOne" | "volume";
  points: IndicatorPoint[];
}

interface IndicatorPoint {
  time: number;                  // Unix timestamp (seconds)
  value: number;
}

interface TradeData {
  entryTime: number;             // Unix timestamp (seconds)
  entryPrice: number;            // decimal
  exitTime?: number;             // Unix timestamp (seconds), null if open
  exitPrice?: number;            // decimal, null if open
  side: "buy" | "sell";
  quantity: number;              // decimal
  pnl?: number;                  // decimal, null if open
  commission: number;            // decimal
  takeProfitPrice?: number;      // decimal, null if not set
  stopLossPrice?: number;        // decimal, null if not set
}
```

## Request Types

### Run Backtest Request

```typescript
// POST /api/backtests
interface RunBacktestRequest {
  assetName: string;
  exchange: string;
  strategyName: string;
  initialCash: number;           // decimal
  startTime: string;             // ISO 8601
  endTime: string;               // ISO 8601
  commissionPerTrade?: number;   // decimal, default 0
  slippageTicks?: number;        // default 0
  timeFrame?: string;            // e.g., "00:15:00"
  strategyParameters?: Record<string, unknown>;
}
```

### Run Optimization Request

```typescript
// POST /api/optimizations
interface RunOptimizationRequest {
  strategyName: string;
  optimizationAxes?: Record<string, OptimizationAxisOverride>;
  dataSubscriptions?: DataSubscription[];
  initialCash: number;           // decimal
  startTime: string;             // ISO 8601
  endTime: string;               // ISO 8601
  commissionPerTrade?: number;   // decimal
  slippageTicks?: number;
  maxDegreeOfParallelism?: number;   // default -1 (auto)
  maxCombinations?: number;          // default 100000
  sortBy?: string;                   // metric name, default "sortinoRatio"
}

type OptimizationAxisOverride =
  | { min: number; max: number; step: number }             // RangeOverride
  | { fixed: unknown }                                      // FixedOverride
  | { values: unknown[] }                                   // DiscreteSetOverride
  | { variants: Record<string, Record<string, OptimizationAxisOverride> | null> };  // ModuleChoiceOverride

interface DataSubscription {
  asset: string;
  exchange: string;
  timeFrame: string;
}
```

### Start Debug Session Request

```typescript
// POST /api/debug-sessions
interface StartDebugSessionRequest {
  assetName: string;
  exchange: string;
  strategyName: string;
  initialCash: number;           // decimal
  startTime: string;             // ISO 8601
  endTime: string;               // ISO 8601
  commissionPerTrade?: number;   // decimal
  slippageTicks?: number;
  timeFrame?: string;
  strategyParameters?: Record<string, unknown>;
}
```

## POST Response Types

The POST endpoints for running backtests and optimizations return the same types as their corresponding GET-by-ID endpoints:

```typescript
// POST /api/backtests → BacktestRun (same shape as GET /api/backtests/{id})
// POST /api/optimizations → OptimizationRun (same shape as GET /api/optimizations/{id})
```

The frontend only needs the response to confirm success (any 2xx) and then invalidates the TanStack Query cache to refetch the list. The full response body is not consumed directly by the "Run New" panel.

## Debug Session Types

### Debug Session DTO

```typescript
// POST /api/debug-sessions → 201 Created
interface DebugSession {
  sessionId: string;             // GUID
  assetName: string;
  strategyName: string;
  createdAt: string;             // ISO 8601
}
```

### Debug Session Status

```typescript
// GET /api/debug-sessions/{id}
interface DebugSessionStatus {
  sessionId: string;
  isRunning: boolean;
  lastSnapshot: DebugSnapshot | null;
  createdAt: string;             // ISO 8601
}
```

### Debug Snapshot (WebSocket message)

```typescript
// Server → Client message type: "snapshot"
interface DebugSnapshot {
  type: "snapshot";
  sessionActive: boolean;
  sequenceNumber: number;
  timestampMs: number;
  subscriptionIndex: number;
  isExportableSubscription: boolean;
  fillsThisBar: number;
  portfolioEquity: number;       // Int64
}
```

### WebSocket Command

```typescript
// Client → Server
interface DebugCommand {
  command: "next" | "next_bar" | "next_trade" | "next_signal"
    | "run_to_timestamp" | "run_to_sequence"
    | "continue" | "pause" | "set_export";
  sequenceNumber?: number;       // Required for "run_to_sequence"
  timestampMs?: number;          // Required for "run_to_timestamp"
  mutations?: boolean;           // Required for "set_export"
}
```

### WebSocket Server Messages

```typescript
// Server → Client (discriminated by `type` field or raw JSONL line)
type ServerMessage =
  | DebugSnapshot                               // type: "snapshot"
  | { type: "error"; message: string }          // type: "error"
  | { type: "set_export_ack"; mutations: boolean }  // type: "set_export_ack"
  // Raw JSONL lines (no `type` field) are backtest events

// Backtest event envelope (raw JSONL from event stream)
interface BacktestEvent<T = unknown> {
  ts: string;                    // ISO 8601 UTC timestamp
  sq: number;                    // Sequence number
  _t: EventType;                 // Event type ID
  src: string;                   // Source identifier
  d: T;                          // Type-specific payload
}
```

## Event Type Payloads

```typescript
type EventType =
  | "bar" | "bar.mut"
  | "ind" | "ind.mut"
  | "sig" | "risk"
  | "ord.place" | "ord.fill" | "ord.cancel" | "ord.reject"
  | "pos"
  | "run.start" | "run.end"
  | "err" | "warn";

// bar, bar.mut
interface BarEventData {
  assetName: string;
  timeFrame: string;
  open: number;                  // Int64
  high: number;                  // Int64
  low: number;                   // Int64
  close: number;                 // Int64
  volume: number;                // Int64
}

// ind, ind.mut
interface IndicatorEventData {
  indicatorName: string;
  measure: "price" | "percent" | "minusOnePlusOne" | "volume";
  values: Record<string, number | null>;
}

// sig
interface SignalEventData {
  signalName: string;
  assetName: string;
  direction: string;
  strength: number;
  reason?: string;
}

// risk
interface RiskEventData {
  assetName: string;
  passed: boolean;
  checkName: string;
  reason?: string;
}

// ord.place
interface OrderPlaceEventData {
  orderId: number;
  assetName: string;
  side: "buy" | "sell";
  type: "market" | "limit" | "stop" | "stopLimit";
  quantity: number;
  limitPrice?: number;
  stopPrice?: number;
}

// ord.fill
interface OrderFillEventData {
  orderId: number;
  assetName: string;
  side: "buy" | "sell";
  price: number;                 // Int64
  quantity: number;
  commission: number;            // Int64
}

// ord.cancel
interface OrderCancelEventData {
  orderId: number;
  assetName: string;
  reason?: string;
}

// ord.reject
interface OrderRejectEventData {
  orderId: number;
  assetName: string;
  reason: string;
}

// pos
interface PositionEventData {
  assetName: string;
  quantity: number;
  averageEntryPrice: number;     // Int64
  realizedPnl: number;           // Int64
}

// run.start
interface RunStartEventData {
  strategyName: string;
  assetName: string;
  initialCash: number;           // Int64
  startTime: string;             // ISO 8601
  endTime: string;               // ISO 8601
  runMode: string;
}

// run.end
interface RunEndEventData {
  totalBarsProcessed: number;
  finalEquity: number;           // Int64
  totalFills: number;
  duration: string;              // ISO 8601 duration
}

// err
interface ErrorEventData {
  message: string;
  stackTrace?: string;
}

// warn
interface WarningEventData {
  message: string;
}
```

## Known Metrics Dictionary Keys

The `metrics` field on `BacktestRun` is a dynamic dictionary. Known keys from the current backend:

| Key | Type | Description |
|-----|------|-------------|
| totalTrades | number | Total completed trades |
| winningTrades | number | Trades with positive P&L |
| losingTrades | number | Trades with negative P&L |
| netProfit | number | Net profit (decimal) |
| grossProfit | number | Sum of winning trades |
| grossLoss | number | Sum of losing trades |
| totalCommissions | number | Total commissions paid |
| totalReturnPct | number | Total return percentage |
| annualizedReturnPct | number | Annualized return percentage |
| sharpeRatio | number | Sharpe ratio |
| sortinoRatio | number | Sortino ratio |
| maxDrawdownPct | number | Maximum drawdown percentage |
| winRatePct | number | Win rate percentage |
| profitFactor | number | Gross profit / gross loss |
| averageWin | number | Average winning trade P&L |
| averageLoss | number | Average losing trade P&L |
| initialCapital | number | Starting capital |
| finalEquity | number | Ending portfolio value |
| tradingDays | number | Number of trading days |
