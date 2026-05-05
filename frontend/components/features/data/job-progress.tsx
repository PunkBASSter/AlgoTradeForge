"use client";

// Phase 3 — single in-flight job's progress card (P3-17). Renders by SSE event type:
//   queued      → "Queued (#N)"
//   started     → "Aggregating…"
//   progress    → "Aggregating <YYYY-MM> … X bars"
//   complete    → toast fires elsewhere; this card un-mounts via clearJob
//   error       → toast fires elsewhere; this card un-mounts via clearJob
//   cancelled   → toast fires elsewhere; this card un-mounts via clearJob (Phase 6)
//
// Phase 6 — Cancel button rendered for non-terminal observations (queued | started | progress).
// Optimistic local state ("Cancelling…") fires on click; the SSE `cancelled` terminal event
// arrives via useJobStream, which un-mounts this component (no manual cleanup needed).

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

// Reviewer Issue F2 — how long after a successful cancel REST we wait for the SSE
// `cancelled` terminal event before falling back to a snapshot poll. Picked to absorb
// the worker's per-record cancellation checkpoint cadence (1 progress event ≤ 1 s) plus
// SSE flush latency. Larger than that risks the user perceiving a stuck card; smaller
// risks racing the SSE and double-clearing.
const CANCEL_FALLBACK_MS = 3_000;

export function JobProgressCard({ jobKey, exchange, outcomeHint }: Props) {
  const [obs, setObs] = useState<JobStreamObservation>({ latest: null, type: null });
  const [isCancelling, setIsCancelling] = useState(false);
  const job = useDataJobsStore((s) => s.jobs[jobKey]);
  const clearJob = useDataJobsStore((s) => s.clearJob);
  const { toast } = useToast();
  useJobStream(jobKey, exchange, setObs);

  // Reviewer Issue F2 — snapshot-poll fallback. After a successful cancel REST we expect
  // the SSE `cancelled` terminal event to land and useJobStream to clearJob. If the SSE
  // dropped (5xx without 410) before the cancel REST went out, no terminal event will
  // arrive and the card would be stuck on "Cancelling…" forever. Once isCancelling is
  // true, schedule a single snapshot fetch CANCEL_FALLBACK_MS later: if the server
  // already considers the job terminal (or the snapshot 404s), clearJob ourselves. The
  // useEffect cleanup cancels the pending timer when the SSE terminal event clears the
  // job ahead of us.
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

  // Cancel button visible only for non-terminal observations AND only once we know the jobId.
  // When the SSE handler hasn't seen any frame yet (`obs.type === null`), we leave the cancel
  // affordance off — sending DELETE with a stale jobId from the store is fine, but rendering
  // a button before "Connecting…" lands risks user-perceived flicker.
  const canCancel = obs.type !== null && CANCELLABLE_TYPES.has(obs.type) && job !== undefined;

  async function handleCancel() {
    if (!job || isCancelling) return;
    setIsCancelling(true);
    try {
      await dataApi.cancelJob(job.jobId);
      // Don't clearJob here — useJobStream's `cancelled` handler does it once the SSE
      // terminal event arrives. That keeps the visible state in sync with the server.
    } catch (err) {
      const message = err instanceof DataApiError ? err.message : String(err);
      toast(`Cancel failed: ${message}`, "error");
      setIsCancelling(false);   // re-enable so the user can retry
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
