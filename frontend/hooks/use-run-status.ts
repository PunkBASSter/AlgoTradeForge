// T039 - Polling hooks for run status tracking

import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { BacktestStatus, OptimizationStatus, RunStatusType } from "@/types/api";

function isTerminal(status: RunStatusType | undefined): boolean {
  return status === "Completed" || status === "Failed" || status === "Cancelled";
}

export function useBacktestStatus(id: string | null) {
  const client = getClient();
  return useQuery<BacktestStatus>({
    queryKey: ["backtest-status", id],
    queryFn: () => client.getBacktestStatus(id!),
    enabled: !!id,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return isTerminal(status) ? false : 5_000;
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
      const status = query.state.data?.status;
      return isTerminal(status) ? false : 30_000;
    },
  });
}
