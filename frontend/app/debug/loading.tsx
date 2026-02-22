// T029 - Debug loading skeleton

import { Skeleton } from "@/components/ui/skeleton";

export default function DebugLoading() {
  return (
    <div className="p-6 space-y-4">
      <Skeleton variant="line" width="200px" />
      <Skeleton variant="rect" height={60} />
      <Skeleton variant="chart" height={500} />
    </div>
  );
}
