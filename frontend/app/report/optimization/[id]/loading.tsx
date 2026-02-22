// T049 - Optimization report loading skeleton

import { Skeleton } from "@/components/ui/skeleton";

export default function OptimizationReportLoading() {
  return (
    <div className="p-6 space-y-4">
      <Skeleton variant="line" width="300px" />
      <Skeleton variant="rect" height="80px" />
      <Skeleton variant="rect" height="400px" />
    </div>
  );
}
