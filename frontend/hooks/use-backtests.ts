// T038 - TanStack Query hooks for backtests

import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useBacktestDetail(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["backtest", id],
    queryFn: () => client.getBacktest(id),
    enabled: !!id,
  });
}

export function useBacktestEquity(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["backtest", id, "equity"],
    queryFn: () => client.getBacktestEquity(id),
    enabled: !!id,
  });
}

export function useBacktestEvents(id: string, enabled = true) {
  const client = getClient();
  return useQuery({
    queryKey: ["backtest", id, "events"],
    queryFn: () => client.getBacktestEvents(id),
    enabled: !!id && enabled,
  });
}
