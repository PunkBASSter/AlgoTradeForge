"use client";

// T059 - Dashboard error boundary

import { Button } from "@/components/ui/button";

export default function DashboardError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <div className="p-6 flex flex-col items-center justify-center gap-4">
      <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
        <h2 className="text-lg font-semibold text-accent-red mb-2">
          Dashboard Error
        </h2>
        <p className="text-sm text-text-secondary mb-4">{error.message}</p>
        <Button variant="primary" onClick={reset}>
          Try Again
        </Button>
      </div>
    </div>
  );
}
