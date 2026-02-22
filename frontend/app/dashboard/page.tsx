"use client";

// T057 + T061 - Dashboard page with mode tabs, filters, runs table, pagination, RunNewPanel

import { useState, useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import { StrategySelector } from "@/components/features/dashboard/strategy-selector";
import { RunFilters, type FilterValues } from "@/components/features/dashboard/run-filters";
import { RunsTable } from "@/components/features/dashboard/runs-table";
import { RunNewPanel } from "@/components/features/dashboard/run-new-panel";
import { Tabs } from "@/components/ui/tabs";
import { Button } from "@/components/ui/button";
import { Pagination } from "@/components/ui/pagination";

const LIMIT = 50;

const modeTabs = [
  { id: "backtest", label: "Backtest" },
  { id: "optimization", label: "Optimization" },
];

const emptyFilters: FilterValues = {
  assetName: "",
  exchange: "",
  timeFrame: "",
  from: "",
  to: "",
};

export default function DashboardPage() {
  const [mode, setMode] = useState<"backtest" | "optimization">("backtest");
  const [selectedStrategy, setSelectedStrategy] = useState<string | null>(null);
  const [filters, setFilters] = useState<FilterValues>(emptyFilters);
  const [offset, setOffset] = useState(0);
  const [runNewOpen, setRunNewOpen] = useState(false);

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
    [selectedStrategy, filters, offset, mode]
  );

  // T051 - useBacktestList
  const backtestQuery = useQuery({
    queryKey: ["backtests", queryParams],
    queryFn: () => client.getBacktests(queryParams),
    enabled: mode === "backtest",
  });

  // T052 - useOptimizationList
  const optimizationQuery = useQuery({
    queryKey: ["optimizations", queryParams],
    queryFn: () => client.getOptimizations(queryParams),
    enabled: mode === "optimization",
  });

  const activeQuery = mode === "backtest" ? backtestQuery : optimizationQuery;

  const handleTabChange = (tab: string) => {
    setMode(tab as "backtest" | "optimization");
    setOffset(0);
  };

  const handleFilterChange = (newFilters: FilterValues) => {
    setFilters(newFilters);
    setOffset(0);
  };

  const handleRunNewSuccess = () => {
    queryClient.invalidateQueries({ queryKey: ["backtests"] });
    queryClient.invalidateQueries({ queryKey: ["optimizations"] });
  };

  return (
    <div className="flex min-h-[calc(100vh-100px)]">
      {/* Sidebar — collapsible on small screens */}
      <aside className="hidden xl:block w-56 shrink-0 border-r border-border-default bg-bg-surface p-4 overflow-y-auto">
        <StrategySelector
          selected={selectedStrategy}
          onSelect={(name) => {
            setSelectedStrategy(name);
            setOffset(0);
          }}
        />
      </aside>

      {/* Main content */}
      <div className="flex-1 p-6 space-y-4 overflow-hidden">
        <div className="flex items-center justify-between">
          <h1 className="text-xl font-bold text-text-primary">Dashboard</h1>
          <Button variant="primary" onClick={() => setRunNewOpen(true)}>
            + Run New
          </Button>
        </div>

        <Tabs
          tabs={modeTabs}
          activeTab={mode}
          onTabChange={handleTabChange}
        />

        <RunFilters filters={filters} onChange={handleFilterChange} />

        <RunsTable
          mode={mode}
          backtests={backtestQuery.data?.items}
          optimizations={optimizationQuery.data?.items}
          isLoading={activeQuery.isLoading}
        />

        {activeQuery.data && (
          <Pagination
            offset={offset}
            limit={LIMIT}
            hasMore={activeQuery.data.hasMore}
            totalCount={activeQuery.data.totalCount}
            onPageChange={setOffset}
          />
        )}
      </div>

      {/* Run New Panel */}
      <RunNewPanel
        open={runNewOpen}
        onClose={() => setRunNewOpen(false)}
        mode={mode}
        onSuccess={handleRunNewSuccess}
      />
    </div>
  );
}
