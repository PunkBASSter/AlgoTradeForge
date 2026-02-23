"use client";

// T047 - OptimizationTrialsTable component

import { useRouter } from "next/navigation";
import { Table, type Column } from "@/components/ui/table";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import type { BacktestRun } from "@/types/api";

interface OptimizationTrialsTableProps {
  trials: BacktestRun[];
}

type TrialRow = BacktestRun & Record<string, unknown>;

const columns: Column<TrialRow>[] = [
  {
    key: "status",
    header: "",
    render: (_v, row) =>
      row.errorMessage ? (
        <span className="text-accent-red" title={row.errorMessage}>
          &#x26A0;
        </span>
      ) : null,
  },
  {
    key: "parameters",
    header: "Parameters",
    render: (_v, row) => (
      <span className="font-mono text-xs">
        {JSON.stringify(row.parameters)}
      </span>
    ),
  },
  {
    key: "sortino",
    header: "Sortino",
    render: (_v, row) => formatNumber(row.metrics?.["sortinoRatio"] ?? 0),
  },
  {
    key: "sharpe",
    header: "Sharpe",
    render: (_v, row) => formatNumber(row.metrics?.["sharpeRatio"] ?? 0),
  },
  {
    key: "profitFactor",
    header: "Profit Factor",
    render: (_v, row) => formatNumber(row.metrics?.["profitFactor"] ?? 0),
  },
  {
    key: "maxDrawdown",
    header: "Max DD",
    render: (_v, row) => formatPercent(row.metrics?.["maxDrawdownPct"] ?? 0),
  },
  {
    key: "winRate",
    header: "Win Rate",
    render: (_v, row) => formatPercent(row.metrics?.["winRatePct"] ?? 0),
  },
  {
    key: "netProfit",
    header: "Net Profit",
    render: (_v, row) => formatNumber(row.metrics?.["netProfit"] ?? 0),
  },
];

export function OptimizationTrialsTable({
  trials,
}: OptimizationTrialsTableProps) {
  const router = useRouter();

  return (
    <Table<TrialRow>
      columns={columns}
      data={trials as TrialRow[]}
      rowKey="id"
      onRowClick={(row) => router.push(`/report/backtest/${row.id}`)}
      emptyMessage="No trials found"
      testId="trials-table"
    />
  );
}
