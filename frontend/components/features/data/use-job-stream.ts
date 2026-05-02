"use client";

// Phase 3 — hook that wires an active jobId to an SSE connection. Resumes from
// `lastEventId` in the persistent store; on terminal event, dispatches a toast and
// clears the entry. On 410 Gone (server retention expired), clears the entry to stop
// reconnect attempts.
//
// Owner: each non-cleared entry in `useDataJobsStore.jobs` is owned by one
// `useJobStream(key)` instance — the DataTabRoot iterates the store and mounts one
// per entry.

import { useEffect, useRef } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useDataJobsStore, type FeedJobKey } from "@/lib/stores/data-jobs-store";
import { connectProgress, GoneError } from "@/lib/services/data-sse-client";
import { useToast } from "@/components/ui/toast";
import type { SseCompletePayload, SseErrorPayload, SseEventPayload } from "@/types/data-tab";

export interface JobStreamObservation {
  /** Most recent payload — drives the progress UI. */
  latest: SseEventPayload | null;
  /** Most recent event type. */
  type: "queued" | "started" | "progress" | "complete" | "error" | null;
}

export function useJobStream(
  key: FeedJobKey,
  exchange: string,
  /** Setter for the parent component's snapshot (if it wants to render progress). */
  onObservation?: (obs: JobStreamObservation) => void,
): void {
  const job = useDataJobsStore((s) => s.jobs[key]);
  const recordEvent = useDataJobsStore((s) => s.recordEvent);
  const clearJob = useDataJobsStore((s) => s.clearJob);
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const onObservationRef = useRef(onObservation);
  onObservationRef.current = onObservation;

  useEffect(() => {
    if (!job) return;

    const ctrl = new AbortController();
    void connectProgress({
      jobId: job.jobId,
      lastEventId: job.lastEventId > 0 ? job.lastEventId : undefined,
      signal: ctrl.signal,
      handlers: {
        onEvent: (id, type, data) => {
          recordEvent(key, id);
          onObservationRef.current?.({ latest: data, type });

          if (type === "complete") {
            const payload = data as SseCompletePayload;
            const overshoot = payload.fidelity.actual_overshoot_pct.toFixed(2);
            toast(`Built ${payload.feed_id} (overshoot ${overshoot}%)`, "success");
            clearJob(key);
            // Re-fetch the affected exchange's asset list so the new column appears.
            queryClient.invalidateQueries({ queryKey: ["data", "exchange-assets", exchange] });
          } else if (type === "error") {
            const payload = data as SseErrorPayload;
            toast(`Aggregation failed: ${payload.message}`, "error");
            clearJob(key);
          }
        },
        onError: (err) => {
          if (err instanceof GoneError) {
            // Job retention expired — server can't replay events for this id. Clear
            // the entry so we don't keep reconnecting forever.
            clearJob(key);
            return;
          }
          // Other errors: surface but don't clear — user can retry by refreshing.
          toast(err.message, "error");
        },
        onClose: () => {
          // Stream closed normally. If the terminal event already cleared the job,
          // there's nothing more to do.
        },
      },
    });

    return () => ctrl.abort();
    // job?.jobId is the right dependency — recordEvent / clearJob / toast / qc are stable refs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [job?.jobId]);
}
