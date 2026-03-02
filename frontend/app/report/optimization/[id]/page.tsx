"use client";

// T048 - Optimization report page

import React from "react";
import { useOptimizationDetail } from "@/hooks/use-optimizations";
import { OptimizationTrialsTable } from "@/components/features/report/optimization-trials-table";
import { Skeleton } from "@/components/ui/skeleton";
import {
  formatCurrency,
  formatDuration,
  formatNumber,
  toTitleCase,
} from "@/lib/utils/format";
import type { FailedTrialDetail } from "@/types/api";

function StatItem({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <span className="text-xs font-medium uppercase tracking-wider text-text-muted">
        {label}
      </span>
      <p className="text-lg font-semibold text-text-primary mt-1">{value}</p>
    </div>
  );
}

function FailedTrialDetails({ details }: { details: FailedTrialDetail[] }) {
  const [open, setOpen] = React.useState(false);

  if (details.length === 0) return null;

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="w-full flex items-center justify-between p-4 text-left"
      >
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
          Failed Trial Details ({details.length})
        </h2>
        <span className="text-text-muted text-xs">{open ? "Hide" : "Show"}</span>
      </button>
      {open && (
        <div className="border-t border-border-default divide-y divide-border-default">
          {details.map((d, i) => (
            <div key={i} className="p-4 space-y-1">
              <div className="flex items-baseline gap-2">
                <span className="text-sm font-semibold text-accent-red">
                  {d.exceptionType}
                </span>
                <span className="text-xs text-text-muted">
                  x{d.occurrenceCount.toLocaleString()}
                </span>
              </div>
              <p className="text-sm text-text-secondary">{d.exceptionMessage}</p>
              <p className="text-xs text-text-muted">
                Sample params:{" "}
                {Object.entries(d.sampleParameters)
                  .map(([k, v]) => `${k}=${JSON.stringify(v)}`)
                  .join(", ")}
              </p>
              {d.stackTrace && (
                <pre className="text-xs text-text-muted mt-2 overflow-x-auto whitespace-pre-wrap">
                  {d.stackTrace}
                </pre>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export default function OptimizationReportPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = React.use(params);

  const {
    data: optimization,
    isLoading,
    error,
  } = useOptimizationDetail(id);

  if (error) {
    return (
      <div className="p-6 flex flex-col items-center justify-center gap-4">
        <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
          <h2 className="text-lg font-semibold text-accent-red mb-2">
            Failed to load optimization
          </h2>
          <p className="text-sm text-text-secondary">
            {error.message}
          </p>
        </div>
      </div>
    );
  }

  if (isLoading || !optimization) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <Skeleton variant="rect" height="80px" />
        <Skeleton variant="rect" height="400px" />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      {/* Title */}
      <div>
        <h1 className="text-xl font-bold text-text-primary">
          Optimization: {optimization.strategyName}
          <span className="ml-2 text-sm font-normal text-text-muted">
            v{optimization.strategyVersion}
          </span>
        </h1>
        <p className="text-sm text-text-secondary mt-1">
          {optimization.assetName} / {optimization.exchange} /{" "}
          {optimization.timeFrame}
          {" -- "}
          {new Date(optimization.dataStart).toLocaleDateString()} to{" "}
          {new Date(optimization.dataEnd).toLocaleDateString()}
        </p>
      </div>

      {/* Run Info */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
          <StatItem
            label="Total Combinations"
            value={formatNumber(optimization.totalCombinations, 0)}
          />
          <StatItem
            label="Kept Trials"
            value={formatNumber(optimization.trials.length, 0)}
          />
          <StatItem
            label="Filtered"
            value={formatNumber(optimization.filteredTrials, 0)}
          />
          <StatItem
            label="Failed"
            value={formatNumber(optimization.failedTrials, 0)}
          />
          <StatItem
            label="Duration"
            value={formatDuration(optimization.durationMs)}
          />
          <StatItem
            label="Started"
            value={new Date(optimization.startedAt).toLocaleString()}
          />
          <StatItem
            label="Completed"
            value={new Date(optimization.completedAt).toLocaleString()}
          />
          <StatItem
            label="Sort By"
            value={toTitleCase(optimization.sortBy)}
          />
          <StatItem
            label="Initial Cash"
            value={formatCurrency(optimization.initialCash)}
          />
          <StatItem
            label="Commission"
            value={formatCurrency(optimization.commission)}
          />
          <StatItem
            label="Slippage Ticks"
            value={formatNumber(optimization.slippageTicks, 0)}
          />
          <StatItem
            label="Max Parallelism"
            value={formatNumber(optimization.maxParallelism, 0)}
          />
        </div>
      </div>

      {/* Failed trial details (collapsible) */}
      <FailedTrialDetails details={optimization.failedTrialDetails} />

      {/* Trials table */}
      <div className="space-y-2">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
          Trials ({optimization.trials.length})
        </h2>
        <OptimizationTrialsTable trials={optimization.trials} />
      </div>
    </div>
  );
}
