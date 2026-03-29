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
