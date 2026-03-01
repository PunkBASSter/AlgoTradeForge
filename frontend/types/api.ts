// T004 - API response and request types

// ---------------------------------------------------------------------------
// Strategy list
// ---------------------------------------------------------------------------

export type StrategyList = string[];

// ---------------------------------------------------------------------------
// Strategy descriptor (from GET /api/strategies/available)
// ---------------------------------------------------------------------------

export interface StrategyDescriptor {
  name: string;
  parameterDefaults: Record<string, unknown>;
  optimizationAxes: ParameterAxisDescriptor[];
}

export interface ParameterAxisDescriptor {
  name: string;
  type: "numeric" | "module";
  min?: number;
  max?: number;
  step?: number;
  clrType?: string;
  variants?: { typeKey: string; axes: ParameterAxisDescriptor[] }[];
}

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
}

export interface OptimizationSubmission {
  id: string;
  totalCombinations: number;
}

export interface BacktestStatus {
  id: string;
  processedBars: number;
  totalBars: number;
  result?: BacktestRun;
}

export interface OptimizationStatus {
  id: string;
  completedCombinations: number;
  totalCombinations: number;
  result?: OptimizationRun;
}

/** Derive run status from backend data (no status field in API response). */
export function deriveBacktestStatus(data: BacktestStatus): RunStatusType {
  if (data.result) {
    if (data.result.runMode === "Cancelled") return "Cancelled";
    if (data.result.errorMessage) return "Failed";
    return "Completed";
  }
  if (data.processedBars === 0) return "Pending";
  return "Running";
}

/** Derive optimization status from backend data. */
export function deriveOptimizationStatus(data: OptimizationStatus): RunStatusType {
  if (data.result) {
    return "Completed";
  }
  if (data.completedCombinations === 0) return "Pending";
  return "Running";
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
  maxTrialsToKeep?: number;
  minProfitFactor?: number | null;
  maxDrawdownPct?: number | null;
  minSharpeRatio?: number | null;
  minSortinoRatio?: number | null;
  minAnnualizedReturnPct?: number | null;
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
