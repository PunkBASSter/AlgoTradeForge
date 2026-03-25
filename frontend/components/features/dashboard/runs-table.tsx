"use client";

import { useRouter } from "next/navigation";
import { Table, type Column } from "@/components/ui/table";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import type { BacktestRun, OptimizationRun, LiveSession } from "@/types/api";

interface RunsTableProps {
  mode: "backtest" | "optimization" | "live";
  backtests?: BacktestRun[];
  optimizations?: OptimizationRun[];
  liveSessions?: LiveSession[];
  isLoading?: boolean;
}

const backtestColumns: Column<BacktestRun>[] = [
  { key: "strategyVersion", header: "Version" },
  { key: "id", header: "Run ID", render: (v) => String(v).substring(0, 8) },
  { key: "dataSubscription.assetName", header: "Asset", render: (_v, row) => row.dataSubscription.assetName },
  { key: "dataSubscription.exchange", header: "Exchange", render: (_v, row) => row.dataSubscription.exchange },
  { key: "dataSubscription.timeFrame", header: "TF", render: (_v, row) => row.dataSubscription.timeFrame },
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
    key: "trades",
    header: "Trades",
    render: (_v, row) => Math.round((row.metrics?.totalTrades ?? 0) / 2),
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
  { key: "dataSubscription.assetName", header: "Asset", render: (_v, row) => row.dataSubscription.assetName },
  { key: "dataSubscription.exchange", header: "Exchange", render: (_v, row) => row.dataSubscription.exchange },
  { key: "dataSubscription.timeFrame", header: "TF", render: (_v, row) => row.dataSubscription.timeFrame },
  { key: "totalCombinations", header: "Combinations" },
  { key: "sortBy", header: "Sort By" },
  {
    key: "durationMs",
    header: "Duration",
    render: (v) => `${(Number(v) / 1000).toFixed(1)}s`,
  },
];

const statusColors: Record<string, string> = {
  Running: "text-green-400",
  Connecting: "text-yellow-400",
  Idle: "text-text-muted",
  Stopping: "text-yellow-400",
  Stopped: "text-red-400",
  Error: "text-red-400",
};

const liveSessionColumns: Column<LiveSession>[] = [
  {
    key: "sessionId",
    header: "Session ID",
    render: (v) => String(v).substring(0, 8),
  },
  { key: "strategyName", header: "Strategy" },
  { key: "assetName", header: "Asset" },
  { key: "exchange", header: "Exchange" },
  { key: "accountName", header: "Account" },
  {
    key: "status",
    header: "Status",
    render: (v) => {
      const s = String(v);
      return (
        <span className={`font-medium ${statusColors[s] ?? "text-text-secondary"}`}>
          {s}
        </span>
      );
    },
  },
  {
    key: "startedAt",
    header: "Started",
    render: (v) => new Date(String(v)).toLocaleString(),
  },
];

export function RunsTable({
  mode,
  backtests,
  optimizations,
  liveSessions,
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

  if (mode === "live") {
    return (
      <Table<LiveSession>
        columns={liveSessionColumns}
        data={liveSessions ?? []}
        rowKey="sessionId"
        onRowClick={(row) =>
          router.push(`/report/live/${encodeURIComponent(row.exchange)}/${row.sessionId}`)
        }
        emptyMessage="No active live sessions"
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
