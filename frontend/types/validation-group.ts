// T067 - Validation group types for per-DSS validation

import type { DataSubscriptionInput } from "@/types/api";

// ---------------------------------------------------------------------------
// Validation group summary (from GET /api/validations/groups list)
// ---------------------------------------------------------------------------

export interface ValidationGroupSummary {
  id: string;
  optimizationGroupId: string;
  strategyName: string;
  thresholdProfileName: string;
  status: string;
  startedAt: string;
  completedAt?: string;
  totalRuns: number;
}

// ---------------------------------------------------------------------------
// Validation group detail (from GET /api/validations/groups/{id})
// ---------------------------------------------------------------------------

export interface ValidationGroupDetail extends ValidationGroupSummary {
  runs: ValidationGroupRunDetail[];
}

export interface ValidationGroupRunDetail {
  id: string;
  optimizationRunId: string;
  dss: DataSubscriptionInput[];
  status: string;
  candidatesIn: number;
  candidatesOut: number;
  compositeScore: number;
  verdict: string;
}

// ---------------------------------------------------------------------------
// Validation group status (polling)
// ---------------------------------------------------------------------------

export interface ValidationGroupStatus {
  id: string;
  status: string;
  runs: ValidationGroupRunStatus[];
}

export interface ValidationGroupRunStatus {
  id: string;
  status: string;
  currentStage: number;
  totalStages: number;
}

// ---------------------------------------------------------------------------
// Validation group submission / request types
// ---------------------------------------------------------------------------

export interface ValidationGroupSubmission {
  id: string;
  totalRuns: number;
}

export interface RunGroupValidationRequest {
  optimizationGroupId: string;
  thresholdProfileName?: string;
  maxTrialsToValidate?: number;
}
