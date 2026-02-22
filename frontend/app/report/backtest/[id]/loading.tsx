// T045 - Backtest report loading skeleton

import { Skeleton } from "@/components/ui/skeleton";

export default function BacktestReportLoading() {
  return (
    <div className="p-6 space-y-6">
      {/* Title skeleton */}
      <Skeleton variant="line" width="300px" />

      {/* Metrics & params grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Skeleton variant="rect" height="200px" />
        <Skeleton variant="rect" height="200px" />
      </div>

      {/* Equity chart skeleton */}
      <Skeleton variant="chart" height="400px" />
    </div>
  );
}
