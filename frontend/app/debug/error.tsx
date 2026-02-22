"use client";

// T030 - Debug error boundary with retry

import { Button } from "@/components/ui/button";

export default function DebugError({
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
          Debug Error
        </h2>
        <p className="text-sm text-text-secondary mb-4">{error.message}</p>
        <Button variant="primary" onClick={reset}>
          Try Again
        </Button>
      </div>
    </div>
  );
}
