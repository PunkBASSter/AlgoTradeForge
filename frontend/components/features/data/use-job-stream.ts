"use client";

// Wires an active jobId to its /jobs/{id}/progress SSE stream. Resumes from the persisted
// `lastEventId` cursor (useJobsStore) and, on any terminal event, invalidates the polled
// jobs list so the panel reflects the final state without waiting for the 5s poll.
//
// The card renders progress from the polled JobEnvelope; this hook's only jobs are (a)
// keeping the resume cursor fresh and (b) prompting a refetch on terminal transitions.

import { useEffect } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useJobsStore } from "@/lib/stores/jobs-store";
import { connectProgress } from "@/lib/services/data-sse-client";

const TERMINAL_TYPES: ReadonlySet<string> = new Set(["complete", "error", "cancelled"]);

export function useJobStream(jobId: string, enabled: boolean = true): void {
  const recordEvent = useJobsStore((s) => s.recordEvent);
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!enabled || !jobId) return;

    const ctrl = new AbortController();
    // Read the cursor at connect time (not via a subscription) so recording new events
    // doesn't retrigger this effect and reconnect the stream.
    const resume = useJobsStore.getState().cursors[jobId]?.lastEventId;
    const resumeId = resume && Number(resume) > 0 ? Number(resume) : undefined;

    void connectProgress({
      jobId,
      lastEventId: resumeId,
      signal: ctrl.signal,
      handlers: {
        onEvent: (id, env) => {
          recordEvent(jobId, String(id));
          if (TERMINAL_TYPES.has(env.type)) {
            queryClient.invalidateQueries({ queryKey: ["data", "jobs"] });
          }
        },
        // Errors (incl. 410 retention-expired) are non-fatal: the 5s useJobs poll remains
        // the source of truth for the panel, so we just stop this stream.
        onError: () => {},
        onClose: () => {},
      },
    });

    return () => ctrl.abort();
    // recordEvent / queryClient are stable refs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [jobId, enabled]);
}
