"use client";

import { useState, useMemo, useEffect } from "react";
import { useRouter } from "next/navigation";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import { RunFilters, type FilterValues } from "@/components/features/dashboard/run-filters";
import { RunsTable } from "@/components/features/dashboard/runs-table";
import { RunNewPanel } from "@/components/features/dashboard/run-new-panel";
import { Tabs } from "@/components/ui/tabs";
import { Button } from "@/components/ui/button";
import { Pagination } from "@/components/ui/pagination";
import { useLiveSessions } from "@/hooks/use-live-sessions";

const LIMIT = 50;

const modeTabs = [
  { id: "backtest", label: "Backtest" },
  { id: "optimization", label: "Optimization" },
  { id: "live", label: "Live Trading" },
];

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
  const [runNewOpen, setRunNewOpen] = useState(false);
  const [rerunConfig, setRerunConfig] = useState<Record<string, unknown> | null>(null);
  const router = useRouter();

  // Check for rerun config from backtest report page
  useEffect(() => {
    if (mode !== "backtest") return;
    const stored = sessionStorage.getItem("rerun-backtest-config");
    if (!stored) return;
    sessionStorage.removeItem("rerun-backtest-config");
    try {
      setRerunConfig(JSON.parse(stored) as Record<string, unknown>);
      setRunNewOpen(true);
    } catch {
      // ignore invalid JSON
    }
  }, [mode]);

  const client = getClient();
  const queryClient = useQueryClient();

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

  const handleTabChange = (tab: string) => {
    router.push(`/${strategy}/${tab}`);
  };

  const handleFilterChange = (newFilters: FilterValues) => {
    setFilters(newFilters);
    setOffset(0);
  };

  const handleRunNewSuccess = () => {
    queryClient.invalidateQueries({ queryKey: ["backtests"] });
    queryClient.invalidateQueries({ queryKey: ["optimizations"] });
    queryClient.invalidateQueries({ queryKey: ["live-sessions"] });
  };

  return (
    <>
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-bold text-text-primary">Dashboard</h1>
        <Button variant="primary" onClick={() => setRunNewOpen(true)}>
          + Run New
        </Button>
      </div>

      <Tabs tabs={modeTabs} activeTab={mode} onTabChange={handleTabChange} />

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

      <RunNewPanel
        open={runNewOpen}
        onClose={() => {
          setRunNewOpen(false);
          setRerunConfig(null);
        }}
        mode={mode}
        selectedStrategy={selectedStrategy}
        onSuccess={handleRunNewSuccess}
        initialContent={rerunConfig}
      />
    </>
  );
}
