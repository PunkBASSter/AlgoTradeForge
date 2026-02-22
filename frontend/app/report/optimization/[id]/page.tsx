"use client";

// T048 - Optimization report page

import React from "react";
import { useOptimizationDetail } from "@/hooks/use-optimizations";
import { OptimizationTrialsTable } from "@/components/features/report/optimization-trials-table";
import { Skeleton } from "@/components/ui/skeleton";
import { formatDuration } from "@/lib/utils/format";

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

      {/* Summary info */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <div>
            <span className="text-xs font-medium uppercase tracking-wider text-text-muted">
              Total Combinations
            </span>
            <p className="text-lg font-semibold text-text-primary mt-1">
              {optimization.totalCombinations.toLocaleString()}
            </p>
          </div>
          <div>
            <span className="text-xs font-medium uppercase tracking-wider text-text-muted">
              Duration
            </span>
            <p className="text-lg font-semibold text-text-primary mt-1">
              {formatDuration(optimization.durationMs)}
            </p>
          </div>
          <div>
            <span className="text-xs font-medium uppercase tracking-wider text-text-muted">
              Sorted By
            </span>
            <p className="text-lg font-semibold text-text-primary mt-1">
              {optimization.sortBy}
            </p>
          </div>
        </div>
      </div>

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
