"use client";

// Single in-flight job's progress card. Renders by SSE event type:
//   queued / started / progress → status line
//   complete / error / cancelled → terminal toast fires elsewhere; this card un-mounts
//                                   when the SSE handler clears the job.
// Cancel button is shown for non-terminal observations only.

import { useEffect, useRef, useState } from "react";
import { useJobStream, type JobStreamObservation } from "./use-job-stream";
import { useDataJobsStore, type FeedJobKey } from "@/lib/stores/data-jobs-store";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { useToast } from "@/components/ui/toast";
import type { JobState } from "@/types/data-tab";

interface Props {
  jobKey: FeedJobKey;
  exchange: string;
  /** Display hint (the FE-composed outcome feed-id like `EqV_1m_1k`). */
  outcomeHint: string;
}

const CANCELLABLE_TYPES = new Set(["queued", "started", "progress"]);
const TERMINAL_JOB_STATES: ReadonlySet<JobState> =
  new Set(["completed", "failed", "cancelled"]);

// How long after a successful cancel REST we wait for the SSE `cancelled` terminal event
// before falling back to a snapshot poll. Sized to absorb the worker's per-record
// cancellation checkpoint cadence (≤ 1s) plus SSE flush latency. Larger risks a
// user-visible stuck card; smaller risks racing the SSE and double-clearing.
const CANCEL_FALLBACK_MS = 3_000;

export function JobProgressCard({ jobKey, exchange, outcomeHint }: Props) {
  const [obs, setObs] = useState<JobStreamObservation>({ latest: null, type: null });
  const [isCancelling, setIsCancelling] = useState(false);
  const job = useDataJobsStore((s) => s.jobs[jobKey]);
  const clearJob = useDataJobsStore((s) => s.clearJob);
  const { toast } = useToast();
  useJobStream(jobKey, exchange, setObs);

  // Snapshot-poll fallback for the cancel path: if the SSE dropped before the cancel
  // REST went out, no `cancelled` terminal event will arrive and the card would be stuck
  // on "Cancelling…". Schedule a single snapshot CANCEL_FALLBACK_MS after isCancelling
  // becomes true; if the server already considers the job terminal (or the snapshot
  // 404s), clear locally. Cleanup cancels the timer if SSE clears the job first.
  const jobIdRef = useRef<string | undefined>(job?.jobId);
  jobIdRef.current = job?.jobId;
  useEffect(() => {
    if (!isCancelling) return;
    const ctrl = new AbortController();
    const timer = setTimeout(async () => {
      const jobId = jobIdRef.current;
      if (!jobId) return;
      try {
        const snap = await dataApi.getJobSnapshot(jobId, ctrl.signal);
        if (TERMINAL_JOB_STATES.has(snap.state)) clearJob(jobKey);
      } catch (err) {
        // 404 → already retention-evicted; clear locally. Other errors: leave alone so
        // the user can retry — better a stuck card than a phantom "cleared" one.
        if (err instanceof DataApiError && err.status === 404) clearJob(jobKey);
      }
    }, CANCEL_FALLBACK_MS);
    return () => {
      clearTimeout(timer);
      ctrl.abort();
    };
  }, [isCancelling, jobKey, clearJob]);

  let line = "Connecting…";
  if (obs.latest?.type === "queued") {
    line = `Queued (#${obs.latest.data.queue_position})`;
  } else if (obs.latest?.type === "started") {
    line = "Aggregating…";
  } else if (obs.latest?.type === "progress") {
    const partition = obs.latest.data.current_partition ?? "—";
    line = `Aggregating ${partition} … ${obs.latest.data.bars_emitted.toLocaleString()} bars`;
  }

  // Hide cancel until at least one SSE frame has landed — avoids a flicker between
  // "Connecting…" and the button.
  const canCancel = obs.type !== null && CANCELLABLE_TYPES.has(obs.type) && job !== undefined;

  async function handleCancel() {
    if (!job || isCancelling) return;
    setIsCancelling(true);
    try {
      await dataApi.cancelJob(job.jobId);
      // Don't clearJob here — useJobStream's `cancelled` handler does it on the SSE
      // terminal event so visible state stays in sync with the server.
    } catch (err) {
      const message = err instanceof DataApiError ? err.message : String(err);
      toast(`Cancel failed: ${message}`, "error");
      setIsCancelling(false);
    }
  }

  return (
    <div className="border border-border-subtle rounded px-3 py-2 bg-bg-surface flex items-center justify-between text-sm gap-3">
      <span className="font-mono text-text-primary truncate" title={outcomeHint}>
        {outcomeHint}
      </span>
      <div className="flex items-center gap-2">
        <span className="text-text-secondary">{isCancelling ? "Cancelling…" : line}</span>
        {canCancel && (
          <button
            type="button"
            onClick={handleCancel}
            disabled={isCancelling}
            className="text-xs px-2 py-1 rounded border border-border-subtle text-text-secondary hover:bg-bg-elevated disabled:opacity-50 disabled:cursor-not-allowed"
            aria-label="Cancel aggregation"
          >
            Cancel
          </button>
        )}
      </div>
    </div>
  );
}
