// T058 - Dashboard loading skeleton

import { Skeleton } from "@/components/ui/skeleton";

export default function DashboardLoading() {
  return (
    <div className="flex min-h-[calc(100vh-100px)]">
      <aside className="w-56 shrink-0 border-r border-border-default bg-bg-surface p-4 space-y-2">
        <Skeleton variant="line" width="120px" />
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} variant="rect" height={32} />
        ))}
      </aside>
      <div className="flex-1 p-6 space-y-4">
        <Skeleton variant="line" width="200px" />
        <Skeleton variant="rect" height={40} />
        <Skeleton variant="rect" height={400} />
      </div>
    </div>
  );
}
