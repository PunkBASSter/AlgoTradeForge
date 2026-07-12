"use client";

// One server-hydrated job of any kind (load / aggregation / materialize / index). Renders
// from the polled JobEnvelope; a live SSE stream (useJobStream) refreshes the list on
// terminal transitions. Cancel (✕) is shown only while the job is non-terminal.

import { useQueryClient } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { useToast } from "@/components/ui/toast";
import { useJobStream } from "./use-job-stream";
import type { JobEnvelope, JobState } from "@/types/data-tab";

const TERMINAL_STATES: ReadonlySet<JobState> = new Set(["complete", "error", "cancelled"]);

const STATE_CHIP: Record<JobState, string> = {
  queued: "bg-bg-elevated text-text-secondary",
  running: "bg-bg-elevated text-text-secondary",
  interrupted: "bg-accent-yellow/20 text-accent-yellow",
  complete: "bg-accent-green/20 text-accent-green",
  error: "bg-accent-red/20 text-accent-red",
  cancelled: "bg-accent-red/20 text-accent-red",
};

function detailLine(job: JobEnvelope): string | null {
  const d = job.progress?.detail ?? null;
  switch (job.kind) {
    case "load": {
      const month = d?.current_month;
      return typeof month === "string" && month ? `Month ${month}` : null;
    }
    case "aggregation": {
      const bars = d?.bars_emitted;
      const partition = d?.current_partition;
      const parts: string[] = [];
      if (typeof partition === "string" && partition) parts.push(partition);
      if (typeof bars === "number") parts.push(`${bars.toLocaleString()} bars`);
      return parts.length > 0 ? parts.join(" · ") : null;
    }
    case "materialize": {
      // M4/M4.1 SSE detail shape: { stage_index, stages_total, phase, stage }.
      const idx = d?.stage_index;
      const total = d?.stages_total;
      const stage = d?.stage;
      if (typeof idx === "number" && typeof total === "number") {
        const label = typeof stage === "string" && stage ? ` (${stage})` : "";
        return `Stage ${idx + 1} of ${total}${label}`;
      }
      return null;
    }
    default:
      return job.progress?.phase ?? null;
  }
}

export function JobCard({ job }: { job: JobEnvelope }) {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const isTerminal = TERMINAL_STATES.has(job.state);
  useJobStream(job.job_id, !isTerminal);

  const done = job.progress?.done ?? 0;
  const total = job.progress?.total ?? 0;
  const pct = total > 0 ? Math.min(100, Math.round((done / total) * 100)) : 0;
  const detail = detailLine(job);

  async function handleCancel() {
    try {
      await dataApi.deleteJob(job.job_id);
      void queryClient.invalidateQueries({ queryKey: ["data", "jobs"] });
    } catch (err) {
      const message = err instanceof DataApiError ? err.message : String(err);
      toast(`Cancel failed: ${message}`, "error");
    }
  }

  return (
    <div className="border border-border-subtle rounded px-3 py-2 bg-bg-surface text-sm space-y-1">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2 min-w-0">
          <span className="text-xs px-2 py-0.5 rounded font-medium bg-bg-elevated text-text-secondary uppercase tracking-wide shrink-0">
            {job.kind}
          </span>
          <span
            className="font-mono text-text-primary truncate"
            title={job.feed_key ?? undefined}
          >
            {job.feed_key ?? job.job_id}
          </span>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <span className={`text-xs px-2 py-0.5 rounded font-medium ${STATE_CHIP[job.state]}`}>
            {job.state}
          </span>
          {!isTerminal && (
            <button
              type="button"
              onClick={() => void handleCancel()}
              className="text-xs px-2 py-1 rounded border border-border-subtle text-text-secondary hover:bg-bg-elevated"
              aria-label="Cancel job"
            >
              ✕
            </button>
          )}
        </div>
      </div>

      {total > 0 && (
        <div
          className="h-1.5 w-full rounded bg-bg-elevated overflow-hidden"
          role="progressbar"
          aria-valuenow={pct}
          aria-valuemin={0}
          aria-valuemax={100}
        >
          <div className="h-full bg-accent-blue transition-all" style={{ width: `${pct}%` }} />
        </div>
      )}

      {detail && <div className="text-text-secondary text-xs">{detail}</div>}

      {job.error && (
        <div
          role="alert"
          className="border border-accent-red/50 bg-accent-red/10 text-accent-red px-2 py-1 rounded text-xs"
        >
          {job.error.message}
        </div>
      )}
    </div>
  );
}
