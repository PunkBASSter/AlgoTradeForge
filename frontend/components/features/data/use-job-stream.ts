"use client";

// Wires an active jobId to an SSE connection. Resumes from `lastEventId` in the
// persistent store; on terminal event toasts and clears the entry. On 410 Gone (server
// retention expired) clears to stop reconnect attempts.
//
// Each non-cleared entry in `useDataJobsStore.jobs` is owned by exactly one
// `useJobStream(key)` instance.

import { useEffect, useRef } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useDataJobsStore, type FeedJobKey } from "@/lib/stores/data-jobs-store";
import { connectProgress, GoneError } from "@/lib/services/data-sse-client";
import { useToast } from "@/components/ui/toast";
import type { SseEventEnvelope } from "@/types/data-tab";

export interface JobStreamObservation {
  /** Most recent envelope — drives the progress UI. Narrow via `latest.type`. */
  latest: SseEventEnvelope | null;
  /** Most recent event type. Same as `latest?.type ?? null`; kept for ergonomic null handling. */
  type: "queued" | "started" | "progress" | "complete" | "error" | "cancelled" | null;
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
        onEvent: (id, env) => {
          recordEvent(key, id);
          onObservationRef.current?.({ latest: env, type: env.type });

          if (env.type === "complete") {
            const overshoot = env.data.fidelity.actual_overshoot_pct.toFixed(2);
            toast(`Built ${env.data.feed_id} (overshoot ${overshoot}%)`, "success");
            clearJob(key);
            const queryKey = ["data", "exchange-assets", exchange];
            queryClient.invalidateQueries({ queryKey });
            // The WebApi proxy holds a ~2s absolute-TTL cache of the catalog payload —
            // the first invalidate hits it and returns stale data. Schedule a follow-up
            // past the TTL so the next refetch bypasses the proxy cache.
            setTimeout(() => {
              queryClient.invalidateQueries({ queryKey });
            }, 2500);
          } else if (env.type === "error") {
            toast(`Aggregation failed: ${env.data.message}`, "error");
            clearJob(key);
          } else if (env.type === "cancelled") {
            toast(`Cancelled (${env.data.reason})`, "info");
            clearJob(key);
          }
        },
        onError: (err) => {
          if (err instanceof GoneError) {
            // Server retention expired; stop reconnecting.
            clearJob(key);
            return;
          }
          // Other errors: surface but don't clear so the user can retry.
          toast(err.message, "error");
        },
        onClose: () => {},
      },
    });

    return () => ctrl.abort();
    // recordEvent / clearJob / toast / qc are stable refs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [job?.jobId]);
}
