"use client";

// T047 - OptimizationTrialsTable component

import { useState, useMemo } from "react";
import { useRouter } from "next/navigation";
import { Table, type Column } from "@/components/ui/table";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import type { BacktestRun } from "@/types/api";

interface OptimizationTrialsTableProps {
  trials: BacktestRun[];
}

const columns: Column<BacktestRun>[] = [
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

export function OptimizationTrialsTable({
  trials,
}: OptimizationTrialsTableProps) {
  const router = useRouter();
  const [assetFilter, setAssetFilter] = useState("");
  const [exchangeFilter, setExchangeFilter] = useState("");
  const [timeFrameFilter, setTimeFrameFilter] = useState("");

  const filtered = useMemo(() => {
    let result = trials;
    if (assetFilter) {
      const lower = assetFilter.toLowerCase();
      result = result.filter((t) =>
        t.assetName.toLowerCase().includes(lower),
      );
    }
    if (exchangeFilter) {
      const lower = exchangeFilter.toLowerCase();
      result = result.filter((t) =>
        t.exchange.toLowerCase().includes(lower),
      );
    }
    if (timeFrameFilter) {
      const lower = timeFrameFilter.toLowerCase();
      result = result.filter((t) =>
        t.timeFrame.toLowerCase().includes(lower),
      );
    }
    return result;
  }, [trials, assetFilter, exchangeFilter, timeFrameFilter]);

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="space-y-1">
          <label htmlFor="trial-filter-asset" className="text-xs text-text-muted">Asset</label>
          <input
            id="trial-filter-asset"
            type="text"
            placeholder="e.g. BTCUSDT"
            value={assetFilter}
            onChange={(e) => setAssetFilter(e.target.value)}
            className="w-32 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
          />
        </div>
        <div className="space-y-1">
          <label htmlFor="trial-filter-exchange" className="text-xs text-text-muted">Exchange</label>
          <input
            id="trial-filter-exchange"
            type="text"
            placeholder="e.g. Binance"
            value={exchangeFilter}
            onChange={(e) => setExchangeFilter(e.target.value)}
            className="w-28 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
          />
        </div>
        <div className="space-y-1">
          <label htmlFor="trial-filter-timeframe" className="text-xs text-text-muted">Timeframe</label>
          <input
            id="trial-filter-timeframe"
            type="text"
            placeholder="e.g. 00:15:00"
            value={timeFrameFilter}
            onChange={(e) => setTimeFrameFilter(e.target.value)}
            className="w-28 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
          />
        </div>
      </div>

      <Table<BacktestRun>
        columns={columns}
        data={filtered}
        rowKey="id"
        onRowClick={(row) => router.push(`/report/backtest/${row.id}`)}
        emptyMessage="No trials found"
        testId="trials-table"
      />
    </div>
  );
}
