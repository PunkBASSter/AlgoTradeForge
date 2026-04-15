// T039 - Optimization group types for per-DSS optimization split

import type { DataSubscriptionInput } from "@/types/api";

// ---------------------------------------------------------------------------
// Optimization group summary (from GET /api/optimizations list)
// ---------------------------------------------------------------------------

export interface OptimizationGroupSummary {
  id: string;
  strategyName: string;
  strategyVersion: string;
  optimizationMethod: string;
  startedAt: string;
  completedAt?: string;
  totalRuns: number;
  completedRuns: number;
  failedRuns: number;
  status: string;
  subscriptions: DataSubscriptionInput[][];
}

// ---------------------------------------------------------------------------
// Optimization group detail (from GET /api/optimizations/groups/{id})
// ---------------------------------------------------------------------------

export interface OptimizationGroupDetail extends OptimizationGroupSummary {
  maxParallelism: number;
  inputJson?: string;
  runs: GroupRunDetail[];
}

export interface GroupRunDetail {
  id: string;
  dss: DataSubscriptionInput[];
  status: string;
  totalCombinations: number;
  keptTrials: number;
  filteredTrials: number;
  failedTrials: number;
  durationMs: number;
  startedAt: string;
  completedAt?: string;
}

// ---------------------------------------------------------------------------
// Optimization group status (polling)
// ---------------------------------------------------------------------------

export interface OptimizationGroupStatus {
  id: string;
  status: string;
  runs: GroupRunStatus[];
}

export interface GroupRunStatus {
  id: string;
  status: string;
  processed: number;
  total: number;
}

// ---------------------------------------------------------------------------
// Optimization group submission (from POST response)
// ---------------------------------------------------------------------------

export interface OptimizationGroupSubmission {
  groupId: string;
  runs: { id: string; dss: DataSubscriptionInput[]; totalCombinations: number }[];
  totalCombinationsPerRun: number;
}
