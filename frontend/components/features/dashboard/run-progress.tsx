"use client";

// T040 - RunProgress component for displaying run progress with polling

import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useBacktestStatus, useOptimizationStatus, useValidationStatusPolling } from "@/hooks/use-run-status";
import { getClient } from "@/lib/services";
import { useToast } from "@/components/ui/toast";
import { StatusBadge } from "@/components/ui/status-badge";
import type { RunStatusType } from "@/types/api";
import { deriveBacktestStatus, deriveOptimizationStatus } from "@/types/api";
import type { ValidationStatus } from "@/types/validation";
import { deriveValidationStatus } from "@/types/validation";

interface RunProgressProps {
  runId: string;
  mode: "backtest" | "optimization" | "validation";
  onComplete?: () => void;
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
  const { toast } = useToast();

  const backtestQuery = useBacktestStatus(mode === "backtest" ? runId : null);
  const optimizationQuery = useOptimizationStatus(mode === "optimization" ? runId : null);
  const validationQuery = useValidationStatusPolling(mode === "validation" ? runId : null);

  const query = mode === "backtest" ? backtestQuery
    : mode === "optimization" ? optimizationQuery
    : validationQuery;

  const { data, isLoading, error } = query;

  const handleCancel = async () => {
    setCancelling(true);
    try {
      if (mode === "backtest") {
        await client.cancelBacktest(runId);
      } else if (mode === "optimization") {
        await client.cancelOptimization(runId);
      } else {
        await client.cancelValidation(runId);
      }
      const queryKey = mode === "backtest"
        ? ["backtest-status", runId]
        : mode === "optimization"
          ? ["optimization-status", runId]
          : ["validation-status", runId];
      await queryClient.invalidateQueries({ queryKey });
    } catch (err) {
      // Swallow 404 (already completed), but surface other errors
      const msg = String(err);
      if (!msg.includes("404")) {
        toast("Failed to cancel run", "error");
      }
    } finally {
      setCancelling(false);
    }
  };

  // Derive status from mode-specific data
  function getDerivedStatus(): RunStatusType | undefined {
    if (!data) return undefined;
    if (mode === "backtest")
      return deriveBacktestStatus(data as import("@/types/api").BacktestStatus);
    if (mode === "optimization")
      return deriveOptimizationStatus(data as import("@/types/api").OptimizationStatus);
    return deriveValidationStatus(data as ValidationStatus);
  }

  const derivedStatus = getDerivedStatus();

  // Notify parent on completion (once only)
  const hasNotifiedRef = useRef(false);
  useEffect(() => {
    if (derivedStatus === "Completed" && onComplete && !hasNotifiedRef.current) {
      hasNotifiedRef.current = true;
      onComplete();
    }
  }, [derivedStatus, onComplete]);

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

  if (!data || !derivedStatus) return null;

  // Extract processed/total counts based on mode
  let processed: number;
  let total: number;
  let label: string;

  if (mode === "backtest") {
    processed = (data as { processedBars: number }).processedBars;
    total = (data as { totalBars: number }).totalBars;
    label = "Bars";
  } else if (mode === "optimization") {
    processed = (data as { completedCombinations: number }).completedCombinations;
    total = (data as { totalCombinations: number }).totalCombinations;
    label = "Combinations";
  } else {
    const vData = data as ValidationStatus;
    processed = vData.currentStage;
    total = vData.totalStages;
    label = "Stages";
  }

  const backtestResult = mode === "backtest" ? (data as import("@/types/api").BacktestStatus).result : undefined;
  const errorMessage = backtestResult?.errorMessage;
  const errorStackTrace = backtestResult?.errorStackTrace;

  return (
    <div className="p-4 rounded-lg border border-border-default bg-bg-panel space-y-3">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <StatusBadge status={derivedStatus} />
          <span className="text-sm text-text-secondary">
            {processed.toLocaleString()} / {total.toLocaleString()} {label}
          </span>
        </div>
      </div>

      {(derivedStatus === "Running" || derivedStatus === "Pending") && (
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

      {errorMessage && (
        <div className="p-3 rounded border border-accent-red bg-red-900/10 space-y-1">
          <p className="text-sm font-medium text-accent-red">Error</p>
          <p className="text-xs text-text-secondary">{errorMessage}</p>
          {errorStackTrace && (
            <pre className="text-xs text-text-muted mt-2 overflow-x-auto whitespace-pre-wrap">
              {errorStackTrace}
            </pre>
          )}
        </div>
      )}
    </div>
  );
}
