import type { RunStatusType } from "@/types/api";

// Phase 2 — Validation result types for WFO, WFM, and parameter analysis

export interface WfoWindowResult {
  windowIndex: number;
  isStartBar: number;
  isEndBar: number;
  oosStartBar: number;
  oosEndBar: number;
  isMetrics: WindowPerformanceMetrics;
  oosMetrics: WindowPerformanceMetrics;
  optimalTrialIndex: number;
  walkForwardEfficiency: number;
  oosProfitable: boolean;
}

export interface WindowPerformanceMetrics {
  totalReturnPct: number;
  annualizedReturnPct: number;
  sharpeRatio: number;
  maxDrawdownPct: number;
  profitFactor: number;
  barCount: number;
}

export interface WfoResult {
  windows: WfoWindowResult[];
  walkForwardEfficiency: number;
  profitableWindowsPct: number;
  maxOosDrawdownExcessPct: number;
  passed: boolean;
}

export interface WfmResult {
  grid: (WfoResult | null)[][];
  periodCounts: number[];
  oosPcts: number[];
  largestContiguousCluster: {
    row: number;
    col: number;
    rows: number;
    cols: number;
  } | null;
  clusterPassCount: number;
  optimalReoptPeriod: number | null;
}

export interface ClusterAnalysisResult {
  primaryClusterConcentration: number;
  clusterCount: number;
  clusterCentroid: Record<string, number>;
  silhouetteScore: number;
}

export interface ParameterHeatmap {
  param1Name: string;
  param2Name: string;
  param1Values: number[];
  param2Values: number[];
  fitnessGrid: number[][];
  plateauScore: number;
}

export interface ParameterSensitivityResult {
  meanFitnessRetention: number;
  heatmaps: ParameterHeatmap[];
  passedDegradationCheck: boolean;
}

export interface StageResultResponse {
  stageNumber: number;
  stageName: string;
  candidatesIn: number;
  candidatesOut: number;
  durationMs: number;
  detailJson?: string;
}

export interface CandidateVerdict {
  trialId: string;
  passed: boolean;
  reasonCode: string | null;
  metrics: Record<string, number>;
}

// Phase 4 — Validation run types

export interface ValidationRun {
  id: string;
  optimizationRunId: string;
  strategyName: string;
  strategyVersion: string | null;
  startedAt: string;
  completedAt: string | null;
  durationMs: number;
  status: string;
  thresholdProfileName: string;
  candidatesIn: number;
  candidatesOut: number;
  compositeScore: number;
  verdict: string;
  verdictSummary: string | null;
  rejections: string[];
  categoryScores: Record<string, number>;
  invocationCount: number;
  errorMessage: string | null;
  stageResults: StageResultResponse[];
}

export interface ValidationStatus {
  id: string;
  status: string;
  currentStage: number;
  totalStages: number;
  result: ValidationRun | null;
}

export function deriveValidationStatus(data: ValidationStatus): RunStatusType {
  if (data.status === "Completed") return "Completed";
  if (data.status === "Cancelled") return "Cancelled";
  if (data.status === "Failed") return "Failed";
  if (data.currentStage === 0) return "Pending";
  return "Running";
}

export interface ValidationSubmission {
  id: string;
  candidateCount: number;
}

export interface RunValidationRequest {
  optimizationRunId: string;
  thresholdProfileName?: string;
}

// List/filter types

export interface ValidationListParams {
  strategyName?: string;
  thresholdProfileName?: string;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

export interface ValidationRunSummary {
  id: string;
  strategyName: string;
  strategyVersion: string | null;
  thresholdProfileName: string;
  startedAt: string;
  completedAt: string | null;
  durationMs: number;
  status: string;
  candidatesIn: number;
  candidatesOut: number;
  compositeScore: number;
  verdict: string;
  verdictSummary: string | null;
  invocationCount: number;
  categoryScores: Record<string, number>;
}

// Equity data for chart rendering

export interface ValidationEquityResponse {
  trials: TrialEquityData[];
  initialEquity: number;
}

export interface TrialEquityData {
  trialIndex: number;
  trialId: string;
  timestamps: number[];
  equity: number[];
  pnlDeltas: number[];
}
