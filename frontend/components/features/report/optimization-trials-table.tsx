"use client";

// T047 - OptimizationTrialsTable component (infinite scroll)

import { useMemo, useRef, useCallback, useEffect } from "react";
import { useRouter } from "next/navigation";
import { Table, type Column } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { useInfiniteOptimizationTrials } from "@/hooks/use-optimizations";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import type { BacktestRun, StartDebugSessionRequest } from "@/types/api";
import { SESSION_KEYS } from "@/lib/constants";
import { Skeleton } from "@/components/ui/skeleton";

const INTERNAL_PARAM_KEYS = new Set(["DataSubscriptions"]);
const CHUNK_SIZE = 1000;

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
  optimizationId: string;
}

export function OptimizationTrialsTable({
  optimizationId,
}: OptimizationTrialsTableProps) {
  const router = useRouter();
  const { toast } = useToast();
  const observerRef = useRef<IntersectionObserver | null>(null);

  const {
    data,
    isLoading,
    isError,
    error,
    hasNextPage,
    isFetchingNextPage,
    fetchNextPage,
  } = useInfiniteOptimizationTrials(optimizationId, { limit: CHUNK_SIZE });

  // Flatten all loaded pages into a single array
  const trials = useMemo(
    () => data?.pages.flatMap((p) => p.items) ?? [],
    [data],
  );

  // Keep mutable refs so the observer callback always reads fresh state
  const fetchRef = useRef(fetchNextPage);
  fetchRef.current = fetchNextPage;
  const hasNextRef = useRef(hasNextPage);
  hasNextRef.current = hasNextPage;
  const fetchingRef = useRef(isFetchingNextPage);
  fetchingRef.current = isFetchingNextPage;

  const sentinelNodeRef = useRef<HTMLDivElement | null>(null);

  // Callback ref — attaches the IntersectionObserver when the sentinel mounts.
  // rootMargin bottom = 100% of viewport height → triggers when the sentinel is
  // one full screen away, giving the network time to respond before the user
  // actually reaches the end.
  const sentinelRef = useCallback((node: HTMLDivElement | null) => {
    sentinelNodeRef.current = node;
    if (observerRef.current) {
      observerRef.current.disconnect();
      observerRef.current = null;
    }
    if (!node) return;

    observerRef.current = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting && hasNextRef.current && !fetchingRef.current) {
          fetchRef.current();
        }
      },
      { rootMargin: "0px 0px 800px 0px" },
    );
    observerRef.current.observe(node);
  }, []);

  // Catch-up: when a fetch completes, check if the sentinel is already visible.
  // Handles rapid scrolling where the observer fired during a fetch and was
  // rejected — without this, loading stalls at the bottom.
  useEffect(() => {
    if (isFetchingNextPage || !hasNextPage) return;
    const el = sentinelNodeRef.current;
    if (!el) return;

    const rect = el.getBoundingClientRect();
    if (rect.top <= window.innerHeight) {
      fetchNextPage();
    }
  }, [isFetchingNextPage, hasNextPage, fetchNextPage]);

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
      { key: "dataSubscriptions.asset", header: "Asset", render: (_v, row) => row.dataSubscriptions[0]?.assetName },
      { key: "dataSubscriptions.exchange", header: "Exchange", render: (_v, row) => row.dataSubscriptions[0]?.exchange },
      { key: "dataSubscriptions.tf", header: "TF", render: (_v, row) => row.dataSubscriptions[0]?.timeFrame },
      {
        key: "fitness",
        header: "Fitness",
        render: (_v, row) => row.metrics?.fitness != null ? formatNumber(row.metrics.fitness, 4) : "—",
      },
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
      {
        key: "debug",
        header: "",
        render: (_v, row) => (
          <button
            onClick={(e) => {
              e.stopPropagation();
              const config: StartDebugSessionRequest = {
                dataSubscriptions: [{
                  assetName: row.dataSubscriptions[0]?.assetName ?? "",
                  exchange: row.dataSubscriptions[0]?.exchange ?? "",
                  timeFrame: toTimeSpan(row.dataSubscriptions[0]?.timeFrame ?? ""),
                }],
                backtestSettings: {
                  initialCash: row.backtestSettings.initialCash,
                  startTime: row.backtestSettings.startTime,
                  endTime: row.backtestSettings.endTime,
                  commissionPerTrade: row.backtestSettings.commissionPerTrade,
                  slippageTicks: row.backtestSettings.slippageTicks,
                },
                strategyName: row.strategyName,
                strategyParameters: Object.fromEntries(
                  Object.entries(row.parameters).filter(
                    ([k]) => !INTERNAL_PARAM_KEYS.has(k),
                  ),
                ),
              };
              sessionStorage.setItem(SESSION_KEYS.DEBUG_CONFIG, JSON.stringify(config));
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

  if (isLoading) {
    return <Skeleton variant="rect" height="400px" />;
  }

  const totalCount = data?.pages[0]?.totalCount ?? 0;

  return (
    <div className="space-y-2">
      <Table<BacktestRun>
        columns={columns}
        data={trials}
        rowKey="id"
        onRowClick={(row) => router.push(`/report/backtest/${row.id}`)}
        emptyMessage="No trials found"
        testId="trials-table"
      />
      {/* Sentinel — observed with rootMargin so loading triggers ~800px before the end */}
      <div ref={sentinelRef} aria-hidden style={{ height: 1 }} />
      {isFetchingNextPage && (
        <div className="flex justify-center py-3">
          <span className="text-sm text-text-muted animate-pulse">Loading more trials...</span>
        </div>
      )}
      {isError && !isFetchingNextPage && (
        <div className="flex items-center justify-between px-4 py-3 rounded-md border border-accent-red bg-red-900/10">
          <span className="text-sm text-accent-red">
            Failed to load more trials{error?.message ? `: ${error.message}` : ""}
          </span>
          <button
            onClick={() => fetchNextPage()}
            className="px-3 py-1 text-sm rounded border border-border-default bg-bg-surface hover:bg-bg-hover text-text-primary transition-colors"
          >
            Retry
          </button>
        </div>
      )}
      {hasNextPage && !isFetchingNextPage && !isError && (
        <div className="flex justify-center py-2">
          <button
            onClick={() => fetchNextPage()}
            className="px-4 py-1.5 text-sm rounded border border-border-default bg-bg-surface hover:bg-bg-hover text-text-muted hover:text-text-primary transition-colors"
          >
            Load more trials
          </button>
        </div>
      )}
      {totalCount > 0 && (
        <div className="text-xs text-text-muted text-right">
          Showing {trials.length} of {totalCount.toLocaleString()}
        </div>
      )}
    </div>
  );
}
