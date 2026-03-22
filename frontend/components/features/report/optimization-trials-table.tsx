"use client";

// T047 - OptimizationTrialsTable component

import { useState, useMemo } from "react";
import { useRouter } from "next/navigation";
import { Table, type Column } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import type { BacktestRun, StartDebugSessionRequest } from "@/types/api";

const INTERNAL_PARAM_KEYS = new Set(["DataSubscriptions"]);

/** Convert shorthand timeframe (e.g. "1h", "15m", "1d") to .NET TimeSpan format ("01:00:00"). */
function toTimeSpan(tf: string): string {
  const match = tf.match(/^(\d+)([smhd])$/);
  if (!match) return tf;
  const n = parseInt(match[1], 10);
  switch (match[2]) {
    case "s": return `00:00:${String(n).padStart(2, "0")}`;
    case "m": return `00:${String(n).padStart(2, "0")}:00`;
    case "h": return `${String(n).padStart(2, "0")}:00:00`;
    case "d": return `${n}.00:00:00`;
    default: return tf;
  }
}

interface OptimizationTrialsTableProps {
  trials: BacktestRun[];
}

export function OptimizationTrialsTable({
  trials,
}: OptimizationTrialsTableProps) {
  const router = useRouter();
  const { toast } = useToast();
  const columns = useMemo<Column<BacktestRun>[]>(
    () => [
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
      {
        key: "debug",
        header: "",
        render: (_v, row) => (
          <button
            onClick={(e) => {
              e.stopPropagation();
              const config: StartDebugSessionRequest = {
                assetName: row.assetName,
                exchange: row.exchange,
                strategyName: row.strategyName,
                initialCash: row.initialCash,
                startTime: row.dataStart,
                endTime: row.dataEnd,
                commissionPerTrade: row.commission,
                slippageTicks: row.slippageTicks,
                timeFrame: toTimeSpan(row.timeFrame),
                strategyParameters: Object.fromEntries(
                  Object.entries(row.parameters).filter(
                    ([k]) => !INTERNAL_PARAM_KEYS.has(k),
                  ),
                ),
              };
              sessionStorage.setItem("debug-session-config", JSON.stringify(config));
              router.push("/debug");
            }}
            className="p-1 rounded hover:bg-bg-surface text-text-muted hover:text-text-primary transition-colors"
            title="Debug with these parameters"
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="8" cy="8" r="3" />
              <path d="M8 2v2M8 12v2M2 8h2M12 8h2M3.8 3.8l1.4 1.4M10.8 10.8l1.4 1.4M3.8 12.2l1.4-1.4M10.8 5.2l1.4-1.4" />
            </svg>
          </button>
        ),
      },
      {
        key: "copyParams",
        header: "",
        render: (_v, row) => (
          <button
            onClick={(e) => {
              e.stopPropagation();
              const filtered = Object.fromEntries(
                Object.entries(row.parameters).filter(
                  ([k]) => !INTERNAL_PARAM_KEYS.has(k),
                ),
              );
              navigator.clipboard.writeText(
                JSON.stringify(filtered, null, 2),
              );
              toast("Parameters copied", "success");
            }}
            className="p-1 rounded hover:bg-bg-surface text-text-muted hover:text-text-primary transition-colors"
            title="Copy parameters to clipboard"
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
              <rect x="5.5" y="5.5" width="8" height="8" rx="1" />
              <path d="M10.5 5.5V3.5a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2" />
            </svg>
          </button>
        ),
      },
    ],
    [toast],
  );

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
