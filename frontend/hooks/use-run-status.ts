// T039 - Polling hooks for run status tracking

import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { BacktestStatus, OptimizationStatus, RunStatusType } from "@/types/api";
import { deriveBacktestStatus, deriveOptimizationStatus } from "@/types/api";
import type { ValidationStatus } from "@/types/validation";
import { deriveValidationStatus } from "@/types/validation";

export const OPTIMIZATION_LIST_POLL_MS = 5_000;
export const OPTIMIZATION_STATUS_POLL_BASE_MS = 5_000;
export const OPTIMIZATION_STATUS_POLL_MAX_MS = 30_000;
export const VALIDATION_STATUS_POLL_BASE_MS = 2_000;
export const VALIDATION_STATUS_POLL_MAX_MS = 15_000;

function isTerminal(status: RunStatusType): boolean {
  return status === "Completed" || status === "Failed" || status === "Cancelled";
}

/** Returns a polling interval that backs off as fetch count increases. */
function backoffInterval(query: { state: { dataUpdateCount: number } }, baseMs: number, maxMs: number): number {
  // Double the interval every 10 fetches, capped at maxMs
  const doublings = Math.floor(query.state.dataUpdateCount / 10);
  return Math.min(baseMs * 2 ** doublings, maxMs);
}

export function useBacktestStatus(id: string | null) {
  const client = getClient();
  return useQuery<BacktestStatus>({
    queryKey: ["backtest-status", id],
    queryFn: () => client.getBacktestStatus(id!),
    enabled: !!id,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (data && isTerminal(deriveBacktestStatus(data))) return false;
      return backoffInterval(query, 3_000, 15_000);
    },
  });
}

export function useOptimizationStatus(id: string | null) {
  const client = getClient();
  return useQuery<OptimizationStatus>({
    queryKey: ["optimization-status", id],
    queryFn: () => client.getOptimizationStatus(id!),
    enabled: !!id,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (data && isTerminal(deriveOptimizationStatus(data))) return false;
      return backoffInterval(query, OPTIMIZATION_STATUS_POLL_BASE_MS, OPTIMIZATION_STATUS_POLL_MAX_MS);
    },
  });
}

export function useValidationStatusPolling(id: string | null) {
  const client = getClient();
  return useQuery<ValidationStatus>({
    queryKey: ["validation-status", id],
    queryFn: () => client.getValidationStatus(id!),
    enabled: !!id,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (data && isTerminal(deriveValidationStatus(data))) return false;
      return backoffInterval(query, VALIDATION_STATUS_POLL_BASE_MS, VALIDATION_STATUS_POLL_MAX_MS);
    },
  });
}
