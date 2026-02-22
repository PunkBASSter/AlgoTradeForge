// T004 - API response and request types

// ---------------------------------------------------------------------------
// Strategy list
// ---------------------------------------------------------------------------

export type StrategyList = string[];

// ---------------------------------------------------------------------------
// Paged response wrapper
// ---------------------------------------------------------------------------

export interface PagedResponse<T> {
  items: T[];
  totalCount: number;
  limit: number;
  offset: number;
  hasMore: boolean;
}

// ---------------------------------------------------------------------------
// Backtest run
// ---------------------------------------------------------------------------

export interface BacktestRun {
  id: string;
  strategyName: string;
  strategyVersion: string;
  parameters: Record<string, unknown>;
  assetName: string;
  exchange: string;
  timeFrame: string;
  initialCash: number;
  commission: number;
  slippageTicks: number;
  startedAt: string;
  completedAt: string;
  dataStart: string;
  dataEnd: string;
  durationMs: number;
  totalBars: number;
  metrics: Record<string, number>;
  hasCandleData: boolean;
  runMode: string;
  optimizationRunId?: string;
  errorMessage?: string;
  errorStackTrace?: string;
}

// ---------------------------------------------------------------------------
// Optimization run
// ---------------------------------------------------------------------------

export interface OptimizationRun {
  id: string;
  strategyName: string;
  strategyVersion: string;
  startedAt: string;
  completedAt: string;
  durationMs: number;
  totalCombinations: number;
  sortBy: string;
  dataStart: string;
  dataEnd: string;
  initialCash: number;
  commission: number;
  slippageTicks: number;
  maxParallelism: number;
  assetName: string;
  exchange: string;
  timeFrame: string;
  trials: BacktestRun[];
}

// ---------------------------------------------------------------------------
// Equity point
// ---------------------------------------------------------------------------

export interface EquityPoint {
  timestampMs: number;
  value: number;
}

// ---------------------------------------------------------------------------
// Events data (from GET /api/backtests/{id}/events)
// ---------------------------------------------------------------------------

export interface EventsData {
  candles: CandleData[];
  indicators: Record<string, IndicatorSeries>;
  trades: TradeData[];
}

export interface CandleData {
  time: number;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface IndicatorSeries {
  measure: "price" | "percent" | "minusOnePlusOne" | "volume";
  points: IndicatorPoint[];
}

export interface IndicatorPoint {
  time: number;
  value: number;
}

export interface TradeData {
  entryTime: number;
  entryPrice: number;
  exitTime?: number;
  exitPrice?: number;
  side: "buy" | "sell";
  quantity: number;
  pnl?: number;
  commission: number;
  takeProfitPrice?: number;
  stopLossPrice?: number;
}

// ---------------------------------------------------------------------------
// Run status types
// ---------------------------------------------------------------------------

export type RunStatusType =
  | "Pending"
  | "Running"
  | "Completed"
  | "Failed"
  | "Cancelled";

export interface BacktestSubmission {
  id: string;
  totalBars: number;
  status: string;
  isDedup: boolean;
}

export interface OptimizationSubmission {
  id: string;
  totalCombinations: number;
  status: string;
  isDedup: boolean;
}

export interface BacktestStatus {
  id: string;
  status: RunStatusType;
  processedBars: number;
  totalBars: number;
  errorMessage?: string;
  errorStackTrace?: string;
  result?: BacktestRun;
}

export interface OptimizationStatus {
  id: string;
  status: RunStatusType;
  completedCombinations: number;
  failedCombinations: number;
  totalCombinations: number;
  errorMessage?: string;
  errorStackTrace?: string;
  result?: OptimizationRun;
}

// ---------------------------------------------------------------------------
// Request types
// ---------------------------------------------------------------------------

export interface RunBacktestRequest {
  assetName: string;
  exchange: string;
  strategyName: string;
  initialCash: number;
  startTime: string;
  endTime: string;
  commissionPerTrade?: number;
  slippageTicks?: number;
  timeFrame?: string;
  strategyParameters?: Record<string, unknown>;
}

export interface RunOptimizationRequest {
  strategyName: string;
  optimizationAxes?: Record<string, OptimizationAxisOverride>;
  dataSubscriptions?: DataSubscription[];
  initialCash: number;
  startTime: string;
  endTime: string;
  commissionPerTrade?: number;
  slippageTicks?: number;
  maxDegreeOfParallelism?: number;
  maxCombinations?: number;
  sortBy?: string;
}

export type OptimizationAxisOverride =
  | { min: number; max: number; step: number }
  | { fixed: unknown }
  | { values: unknown[] }
  | { variants: Record<string, Record<string, OptimizationAxisOverride> | null> };

export interface DataSubscription {
  asset: string;
  exchange: string;
  timeFrame: string;
}

// ---------------------------------------------------------------------------
// Debug session types
// ---------------------------------------------------------------------------

export interface StartDebugSessionRequest {
  assetName: string;
  exchange: string;
  strategyName: string;
  initialCash: number;
  startTime: string;
  endTime: string;
  commissionPerTrade?: number;
  slippageTicks?: number;
  timeFrame?: string;
  strategyParameters?: Record<string, unknown>;
}

export interface DebugSession {
  sessionId: string;
  assetName: string;
  strategyName: string;
  createdAt: string;
}

export interface DebugSessionStatus {
  sessionId: string;
  isRunning: boolean;
  lastSnapshot: DebugSnapshot | null;
  createdAt: string;
}

export interface DebugSnapshot {
  type: "snapshot";
  sessionActive: boolean;
  sequenceNumber: number;
  timestampMs: number;
  subscriptionIndex: number;
  isExportableSubscription: boolean;
  fillsThisBar: number;
  portfolioEquity: number;
}

export interface DebugCommand {
  command: "next" | "next_bar" | "next_trade" | "next_signal"
    | "run_to_timestamp" | "run_to_sequence"
    | "continue" | "pause" | "set_export";
  sequenceNumber?: number;
  timestampMs?: number;
  mutations?: boolean;
}

export type ServerMessage =
  | DebugSnapshot
  | { type: "error"; message: string }
  | { type: "set_export_ack"; mutations: boolean };
