"use client";

import { useLoadJob } from "@/hooks/use-load-job";
import { DataApiError } from "@/lib/services/data-api";

interface Props {
  jobId: string;
  onDismiss: () => void;
}

const STATE_LABELS: Record<string, string> = {
  queued: "Queued",
  running: "Running",
  complete: "Complete",
  error: "Failed",
};

export function LoadJobCard({ jobId, onDismiss }: Props) {
  const { data, error, isLoading } = useLoadJob(jobId);

  const is404 =
    error instanceof DataApiError && error.status === 404;

  const headerLine = data
    ? `${data.symbol} ${data.feed_name} ${data.interval}`
    : jobId;

  const stateLabel = data ? (STATE_LABELS[data.state] ?? data.state) : null;
  const isRunning = data?.state === "running" || data?.state === "queued";
  const isTerminal = data?.state === "complete" || data?.state === "error";

  return (
    <div className="border border-border-subtle rounded px-3 py-2 bg-bg-surface text-sm space-y-1">
      <div className="flex items-center justify-between gap-3">
        <span className="font-mono text-text-primary truncate" title={headerLine}>
          {headerLine}
        </span>
        <div className="flex items-center gap-2 shrink-0">
          {stateLabel && (
            <span
              className={`text-xs px-2 py-0.5 rounded font-medium ${
                data?.state === "complete"
                  ? "bg-accent-green/20 text-accent-green"
                  : data?.state === "error"
                    ? "bg-accent-red/20 text-accent-red"
                    : "bg-bg-elevated text-text-secondary"
              }`}
            >
              {stateLabel}
            </span>
          )}
          {is404 && (
            <span className="text-xs px-2 py-0.5 rounded font-medium bg-bg-elevated text-text-muted">
              Expired
            </span>
          )}
          {isLoading && !data && (
            <span className="text-xs text-text-muted">Loading…</span>
          )}
          <button
            type="button"
            onClick={onDismiss}
            className="text-xs px-2 py-1 rounded border border-border-subtle text-text-secondary hover:bg-bg-elevated"
            aria-label="Dismiss job"
          >
            Dismiss
          </button>
        </div>
      </div>

      {isRunning && data && (
        <div className="text-text-secondary text-xs">
          {data.months_done}/{data.months_total} months
          {data.current_month ? ` — ${data.current_month}` : ""}
        </div>
      )}

      {isTerminal && data && (
        <div className="text-text-secondary text-xs">
          {data.months_done}/{data.months_total} months
        </div>
      )}

      {data?.state === "error" && data.error_message && (
        <div
          role="alert"
          className="border border-accent-red/50 bg-accent-red/10 text-accent-red px-2 py-1 rounded text-xs"
        >
          {data.error_message}
        </div>
      )}

      {is404 && (
        <div className="text-text-muted text-xs">
          Job record has expired from the server registry.
        </div>
      )}
    </div>
  );
}
