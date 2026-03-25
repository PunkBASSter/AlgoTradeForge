"use client";

import { useState, useMemo, useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import { RunFilters, type FilterValues } from "@/components/features/dashboard/run-filters";
import { RunsTable } from "@/components/features/dashboard/runs-table";
import { Pagination } from "@/components/ui/pagination";
import { useLiveSessions } from "@/hooks/use-live-sessions";
import { useRunNew } from "@/contexts/run-new-context";

const LIMIT = 50;

const emptyFilters: FilterValues = {
  assetName: "",
  exchange: "",
  timeFrame: "",
  from: "",
  to: "",
};

interface DashboardContentProps {
  strategy: string;
  mode: "backtest" | "optimization" | "live";
}

export function DashboardContent({ strategy, mode }: DashboardContentProps) {
  const selectedStrategy = strategy === "all" ? null : strategy;
  const [filters, setFilters] = useState<FilterValues>(emptyFilters);
  const [offset, setOffset] = useState(0);
  const { openWithContent } = useRunNew();

  // Check for rerun config from backtest report page
  useEffect(() => {
    if (mode !== "backtest") return;
    const stored = sessionStorage.getItem("rerun-backtest-config");
    if (!stored) return;
    sessionStorage.removeItem("rerun-backtest-config");
    try {
      const config = JSON.parse(stored) as Record<string, unknown>;
      openWithContent(config);
    } catch {
      // ignore invalid JSON
    }
  }, [mode, openWithContent]);

  const client = getClient();

  const queryParams = useMemo(
    () => ({
      strategyName: selectedStrategy ?? undefined,
      assetName: filters.assetName || undefined,
      exchange: filters.exchange || undefined,
      timeFrame: filters.timeFrame || undefined,
      from: filters.from || undefined,
      to: filters.to || undefined,
      limit: LIMIT,
      offset,
      ...(mode === "backtest" ? { standaloneOnly: true } : {}),
    }),
    [selectedStrategy, filters, offset, mode],
  );

  const backtestQuery = useQuery({
    queryKey: ["backtests", queryParams],
    queryFn: () => client.getBacktests(queryParams),
    enabled: mode === "backtest",
  });

  const optimizationQuery = useQuery({
    queryKey: ["optimizations", queryParams],
    queryFn: () => client.getOptimizations(queryParams),
    enabled: mode === "optimization",
  });

  const liveSessionsQuery = useLiveSessions(mode === "live");

  const activeQuery = mode === "live" ? null : mode === "backtest" ? backtestQuery : optimizationQuery;

  const handleFilterChange = (newFilters: FilterValues) => {
    setFilters(newFilters);
    setOffset(0);
  };

  return (
    <>
      {mode !== "live" && (
        <RunFilters filters={filters} onChange={handleFilterChange} />
      )}

      <RunsTable
        mode={mode}
        backtests={backtestQuery.data?.items}
        optimizations={optimizationQuery.data?.items}
        liveSessions={liveSessionsQuery.data?.sessions}
        isLoading={mode === "live" ? liveSessionsQuery.isLoading : (activeQuery?.isLoading ?? false)}
      />

      {activeQuery?.data && (
        <Pagination
          offset={offset}
          limit={LIMIT}
          hasMore={activeQuery.data.hasMore}
          totalCount={activeQuery.data.totalCount}
          onPageChange={setOffset}
        />
      )}
    </>
  );
}
