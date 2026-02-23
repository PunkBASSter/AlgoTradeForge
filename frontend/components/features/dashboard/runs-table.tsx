"use client";

// T055 - Runs table component for backtests and optimizations

import { useRouter } from "next/navigation";
import { Table, type Column } from "@/components/ui/table";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import type { BacktestRun, OptimizationRun } from "@/types/api";

interface RunsTableProps {
  mode: "backtest" | "optimization";
  backtests?: BacktestRun[];
  optimizations?: OptimizationRun[];
  isLoading?: boolean;
}

const backtestColumns: Column<BacktestRun>[] = [
  { key: "strategyVersion", header: "Version" },
  { key: "id", header: "Run ID", render: (v) => String(v).substring(0, 8) },
  { key: "assetName", header: "Asset" },
  { key: "exchange", header: "Exchange" },
  { key: "timeFrame", header: "TF" },
  {
    key: "sortino",
    header: "Sortino",
    render: (_v, row) => formatNumber(row.metrics?.sortinoRatio ?? 0),
  },
  {
    key: "sharpe",
    header: "Sharpe",
    render: (_v, row) => formatNumber(row.metrics?.sharpeRatio ?? 0),
  },
  {
    key: "profitFactor",
    header: "PF",
    render: (_v, row) => formatNumber(row.metrics?.profitFactor ?? 0),
  },
  {
    key: "maxDD",
    header: "Max DD",
    render: (_v, row) => formatPercent(row.metrics?.maxDrawdownPct ?? 0),
  },
  {
    key: "winRate",
    header: "Win Rate",
    render: (_v, row) => formatPercent(row.metrics?.winRatePct ?? 0),
  },
  {
    key: "netProfit",
    header: "Net Profit",
    render: (_v, row) => formatNumber(row.metrics?.netProfit ?? 0),
  },
];

const optimizationColumns: Column<OptimizationRun>[] = [
  { key: "strategyVersion", header: "Version" },
  { key: "id", header: "Run ID", render: (v) => String(v).substring(0, 8) },
  { key: "assetName", header: "Asset" },
  { key: "exchange", header: "Exchange" },
  { key: "timeFrame", header: "TF" },
  { key: "totalCombinations", header: "Combinations" },
  { key: "sortBy", header: "Sort By" },
  {
    key: "durationMs",
    header: "Duration",
    render: (v) => `${(Number(v) / 1000).toFixed(1)}s`,
  },
];

export function RunsTable({
  mode,
  backtests,
  optimizations,
  isLoading,
}: RunsTableProps) {
  const router = useRouter();

  if (isLoading) {
    return (
      <div className="p-8 text-center text-text-muted text-sm">
        Loading runs...
      </div>
    );
  }

  if (mode === "backtest") {
    return (
      <Table<BacktestRun>
        columns={backtestColumns}
        data={backtests ?? []}
        rowKey="id"
        onRowClick={(row) => router.push(`/report/backtest/${row.id}`)}
        emptyMessage="No backtest runs found"
        testId="runs-table"
      />
    );
  }

  return (
    <Table<OptimizationRun>
      columns={optimizationColumns}
      data={optimizations ?? []}
      rowKey="id"
      onRowClick={(row) => router.push(`/report/optimization/${row.id}`)}
      emptyMessage="No optimization runs found"
      testId="runs-table"
    />
  );
}
