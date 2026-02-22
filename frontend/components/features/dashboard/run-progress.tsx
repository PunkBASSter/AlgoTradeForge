"use client";

// T040 - RunProgress component for displaying run progress with polling

import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useBacktestStatus, useOptimizationStatus } from "@/hooks/use-run-status";
import { getClient } from "@/lib/services";
import type { RunStatusType } from "@/types/api";

interface RunProgressProps {
  runId: string;
  mode: "backtest" | "optimization";
  onComplete?: () => void;
}

function StatusBadge({ status }: { status: RunStatusType }) {
  const colors: Record<RunStatusType, string> = {
    Pending: "bg-yellow-900/30 text-yellow-400 border-yellow-700",
    Running: "bg-blue-900/30 text-blue-400 border-blue-700",
    Completed: "bg-green-900/30 text-green-400 border-green-700",
    Failed: "bg-red-900/30 text-red-400 border-red-700",
    Cancelled: "bg-neutral-800 text-text-muted border-border-default",
  };
  return (
    <span
      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${colors[status]}`}
    >
      {status}
    </span>
  );
}

function ProgressBar({ processed, total }: { processed: number; total: number }) {
  const pct = total > 0 ? Math.min((processed / total) * 100, 100) : 0;
  return (
    <div className="w-full bg-bg-surface rounded-full h-2 overflow-hidden">
      <div
        className="h-full bg-accent-blue rounded-full transition-all duration-500"
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}

export function RunProgress({ runId, mode, onComplete }: RunProgressProps) {
  const [cancelling, setCancelling] = useState(false);
  const queryClient = useQueryClient();
  const client = getClient();

  const backtestQuery = useBacktestStatus(mode === "backtest" ? runId : null);
  const optimizationQuery = useOptimizationStatus(mode === "optimization" ? runId : null);

  const isBacktest = mode === "backtest";
  const data = isBacktest ? backtestQuery.data : optimizationQuery.data;
  const isLoading = isBacktest ? backtestQuery.isLoading : optimizationQuery.isLoading;
  const error = isBacktest ? backtestQuery.error : optimizationQuery.error;

  const handleCancel = async () => {
    setCancelling(true);
    try {
      if (isBacktest) {
        await client.cancelBacktest(runId);
      } else {
        await client.cancelOptimization(runId);
      }
      const queryKey = isBacktest
        ? ["backtest-status", runId]
        : ["optimization-status", runId];
      await queryClient.invalidateQueries({ queryKey });
    } catch {
      // Cancellation may fail if already completed
    } finally {
      setCancelling(false);
    }
  };

  // Notify parent on completion
  const status = data?.status;
  if (status === "Completed" && onComplete) {
    onComplete();
  }

  if (isLoading) {
    return (
      <div className="p-4 rounded-lg border border-border-default bg-bg-panel space-y-3">
        <div className="flex items-center gap-2">
          <div className="h-4 w-16 bg-bg-surface animate-pulse rounded" />
          <div className="h-4 w-32 bg-bg-surface animate-pulse rounded" />
        </div>
        <div className="h-2 bg-bg-surface animate-pulse rounded-full" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4 rounded-lg border border-accent-red bg-bg-panel">
        <p className="text-sm text-accent-red">Failed to fetch status: {error.message}</p>
      </div>
    );
  }

  if (!data) return null;

  const processed = isBacktest
    ? (data as { processedBars: number }).processedBars
    : (data as { completedCombinations: number }).completedCombinations;
  const total = isBacktest
    ? (data as { totalBars: number }).totalBars
    : (data as { totalCombinations: number }).totalCombinations;
  const failed = !isBacktest
    ? (data as { failedCombinations: number }).failedCombinations
    : 0;

  const label = isBacktest ? "Bars" : "Combinations";

  return (
    <div className="p-4 rounded-lg border border-border-default bg-bg-panel space-y-3">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <StatusBadge status={data.status} />
          <span className="text-sm text-text-secondary">
            {processed.toLocaleString()} / {total.toLocaleString()} {label}
          </span>
          {failed > 0 && (
            <span className="text-xs text-accent-red">
              ({failed} failed)
            </span>
          )}
        </div>
      </div>

      {(data.status === "Running" || data.status === "Pending") && (
        <>
          <ProgressBar processed={processed} total={total} />
          <button
            onClick={handleCancel}
            disabled={cancelling}
            className="text-xs text-text-muted hover:text-accent-red transition-colors disabled:opacity-50"
          >
            {cancelling ? "Cancelling..." : "Cancel run"}
          </button>
        </>
      )}

      {data.errorMessage && (
        <div className="p-3 rounded border border-accent-red bg-red-900/10 space-y-1">
          <p className="text-sm font-medium text-accent-red">Error</p>
          <p className="text-xs text-text-secondary">{data.errorMessage}</p>
          {data.errorStackTrace && (
            <pre className="text-xs text-text-muted mt-2 overflow-x-auto whitespace-pre-wrap">
              {data.errorStackTrace}
            </pre>
          )}
        </div>
      )}
    </div>
  );
}
