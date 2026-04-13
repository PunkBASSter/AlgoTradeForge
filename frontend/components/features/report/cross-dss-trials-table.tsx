"use client";

// T054 - CrossDssTrialsTable — optimization group trials table with DSS column

import { useMemo, useRef, useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useToast } from "@/components/ui/toast";
import { useRunNew } from "@/contexts/run-new-context";
import { useInfiniteOptimizationGroupTrials } from "@/hooks/use-optimization-groups";
import { formatNumber, formatPercent } from "@/lib/utils/format";
import { openTrialAsBacktest } from "@/components/features/report/trial-backtest-panel";
import { toTimeSpan } from "@/lib/utils/timeframe";
import type { BacktestRun, StartDebugSessionRequest } from "@/types/api";
import { SESSION_KEYS } from "@/lib/constants";
import { Skeleton } from "@/components/ui/skeleton";

const INTERNAL_PARAM_KEYS = new Set(["DataSubscriptions"]);
const CHUNK_SIZE = 1000;

type SortDirection = "asc" | "desc";

interface SortState {
  key: string;
  direction: SortDirection;
}

/** SVG sort indicator arrow. */
function SortIcon({ direction, active }: { direction: SortDirection; active: boolean }) {
  return (
    <svg
      width="10"
      height="10"
      viewBox="0 0 10 10"
      fill="none"
      className={`inline-block ml-1 transition-colors ${active ? "text-text-primary" : "text-text-muted/40"}`}
    >
      {direction === "asc" ? (
        <path d="M5 2L9 8H1L5 2Z" fill="currentColor" />
      ) : (
        <path d="M5 8L1 2H9L5 8Z" fill="currentColor" />
      )}
    </svg>
  );
}

/** Sortable table header cell. */
function SortableHeader({
  label,
  sortKey,
  sortState,
  onSort,
  className = "",
}: {
  label: string;
  sortKey: string;
  sortState: SortState | null;
  onSort: (key: string) => void;
  className?: string;
}) {
  const active = sortState?.key === sortKey;
  const direction = active ? sortState.direction : "desc";
  return (
    <th
      className={`px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted cursor-pointer select-none hover:text-text-primary transition-colors ${className}`}
      onClick={() => onSort(sortKey)}
    >
      {label}
      <SortIcon direction={direction} active={active} />
    </th>
  );
}

/** Format params object as a compact key=value string. */
function formatParams(params: Record<string, unknown>): string {
  return Object.entries(params)
    .filter(([k]) => !INTERNAL_PARAM_KEYS.has(k))
    .map(([k, v]) => {
      if (typeof v === "number") return `${k}=${formatNumber(v, v % 1 === 0 ? 0 : 2)}`;
      if (typeof v === "object" && v !== null) return `${k}={...}`;
      return `${k}=${String(v)}`;
    })
    .join(", ");
}

/** Format DSS as a compact string (asset/exchange/tf). */
function formatDss(row: BacktestRun): string {
  const ds = row.dataSubscriptions[0];
  if (!ds) return "\u2014";
  return `${ds.assetName}/${ds.exchange}/${ds.timeFrame}`;
}

/** Metric accessor for sort. */
function getMetricValue(row: BacktestRun, key: string): number {
  switch (key) {
    case "fitness": return row.metrics?.fitness ?? -Infinity;
    case "sortino": return row.metrics?.sortinoRatio ?? 0;
    case "sharpe": return row.metrics?.sharpeRatio ?? 0;
    case "profitFactor": return row.metrics?.profitFactor ?? 0;
    case "maxDD": return row.metrics?.maxDrawdownPct ?? 0;
    case "winRate": return row.metrics?.winRatePct ?? 0;
    case "trades": return row.metrics?.totalTrades ?? 0;
    case "netProfit": return row.metrics?.netProfit ?? 0;
    default: return 0;
  }
}

interface CrossDssTrialsTableProps {
  groupId: string;
}

export function CrossDssTrialsTable({
  groupId,
}: CrossDssTrialsTableProps) {
  const router = useRouter();
  const { toast } = useToast();
  const { openWithContent } = useRunNew();
  const observerRef = useRef<IntersectionObserver | null>(null);
  const [sortState, setSortState] = useState<SortState | null>(null);

  const {
    data,
    isLoading,
    isError,
    error,
    hasNextPage,
    isFetchingNextPage,
    fetchNextPage,
  } = useInfiniteOptimizationGroupTrials(groupId, { limit: CHUNK_SIZE });

  // Flatten all loaded pages into a single array
  const trials = useMemo(
    () => data?.pages.flatMap((p) => p.items) ?? [],
    [data],
  );

  // Client-side sort
  const sortedTrials = useMemo(() => {
    if (!sortState) return trials;
    const { key, direction } = sortState;
    const sorted = [...trials].sort((a, b) => {
      const aVal = getMetricValue(a, key);
      const bVal = getMetricValue(b, key);
      return direction === "asc" ? aVal - bVal : bVal - aVal;
    });
    return sorted;
  }, [trials, sortState]);

  const handleSort = useCallback((key: string) => {
    setSortState((prev) => {
      if (prev?.key === key) {
        return { key, direction: prev.direction === "desc" ? "asc" : "desc" };
      }
      return { key, direction: "desc" };
    });
  }, []);

  // Keep mutable refs so the observer callback always reads fresh state
  const fetchRef = useRef(fetchNextPage);
  fetchRef.current = fetchNextPage;
  const hasNextRef = useRef(hasNextPage);
  hasNextRef.current = hasNextPage;
  const fetchingRef = useRef(isFetchingNextPage);
  fetchingRef.current = isFetchingNextPage;

  const sentinelNodeRef = useRef<HTMLDivElement | null>(null);

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

  useEffect(() => {
    if (isFetchingNextPage || !hasNextPage) return;
    const el = sentinelNodeRef.current;
    if (!el) return;

    const rect = el.getBoundingClientRect();
    if (rect.top <= window.innerHeight) {
      fetchNextPage();
    }
  }, [isFetchingNextPage, hasNextPage, fetchNextPage]);

  if (isLoading) {
    return <Skeleton variant="rect" height="400px" />;
  }

  const totalCount = data?.pages[0]?.totalCount ?? 0;

  return (
    <div className="space-y-2">
      <div className="overflow-x-auto rounded-md border border-border-default" data-testid="cross-dss-trials-table">
        <table className="w-full text-left text-sm">
          <thead>
            <tr className="border-b border-border-default bg-bg-panel">
              <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted" />
              <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Run ID</th>
              <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">DSS</th>
              <SortableHeader label="Fitness" sortKey="fitness" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="Sortino" sortKey="sortino" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="Sharpe" sortKey="sharpe" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="PF" sortKey="profitFactor" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="Max DD" sortKey="maxDD" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="Win Rate" sortKey="winRate" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="Trades" sortKey="trades" sortState={sortState} onSort={handleSort} />
              <SortableHeader label="Net Profit" sortKey="netProfit" sortState={sortState} onSort={handleSort} />
              <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Params</th>
              <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted" />
              <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted" />
            </tr>
          </thead>
          <tbody className="divide-y divide-border-subtle">
            {sortedTrials.length === 0 ? (
              <tr>
                <td colSpan={14} className="px-4 py-8 text-center text-text-muted">
                  No trials found
                </td>
              </tr>
            ) : (
              sortedTrials.map((row) => (
                <tr
                  key={row.id}
                  onClick={() => router.push(`/report/backtest/${row.id}`)}
                  className="bg-bg-surface transition-colors hover:bg-bg-hover cursor-pointer"
                >
                  <td className="px-4 py-3 text-text-primary">
                    {row.errorMessage ? (
                      <span className="text-accent-red" title={row.errorMessage}>
                        &#x26A0;
                      </span>
                    ) : null}
                  </td>
                  <td className="px-4 py-3 text-text-primary">
                    <button
                      onClick={(e) => {
                        e.stopPropagation();
                        openTrialAsBacktest(row, openWithContent);
                      }}
                      className="text-accent-blue hover:underline"
                      title="Open as new backtest"
                    >
                      {row.id.substring(0, 8)}
                    </button>
                  </td>
                  <td className="px-4 py-3 text-text-secondary text-xs whitespace-nowrap">
                    {formatDss(row)}
                  </td>
                  <td className="px-4 py-3 text-text-primary">
                    {row.metrics?.fitness != null ? formatNumber(row.metrics.fitness, 4) : "\u2014"}
                  </td>
                  <td className="px-4 py-3 text-text-primary">{formatNumber(row.metrics?.sortinoRatio ?? 0)}</td>
                  <td className="px-4 py-3 text-text-primary">{formatNumber(row.metrics?.sharpeRatio ?? 0)}</td>
                  <td className="px-4 py-3 text-text-primary">{formatNumber(row.metrics?.profitFactor ?? 0)}</td>
                  <td className="px-4 py-3 text-text-primary">{formatPercent(row.metrics?.maxDrawdownPct ?? 0)}</td>
                  <td className="px-4 py-3 text-text-primary">{formatPercent(row.metrics?.winRatePct ?? 0)}</td>
                  {/* totalTrades counts individual fills (buy+sell); divide by 2 for round-trip trades */}
                  <td className="px-4 py-3 text-text-primary">{Math.round((row.metrics?.totalTrades ?? 0) / 2)}</td>
                  <td className="px-4 py-3 text-text-primary">{formatNumber(row.metrics?.netProfit ?? 0)}</td>
                  <td className="px-4 py-3 text-text-muted text-xs max-w-[300px] truncate" title={formatParams(row.parameters)}>
                    {formatParams(row.parameters)}
                  </td>
                  <td className="px-4 py-3 text-text-primary">
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
                  </td>
                  <td className="px-4 py-3 text-text-primary">
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
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
      {/* Sentinel */}
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
          Showing {sortedTrials.length} of {totalCount.toLocaleString()}
        </div>
      )}
    </div>
  );
}
