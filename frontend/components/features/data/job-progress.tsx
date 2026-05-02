"use client";

// Phase 3 — single in-flight job's progress card (P3-17). Renders by SSE event type:
//   queued      → "Queued (#N)"
//   started     → "Aggregating…"
//   progress    → "Aggregating <YYYY-MM> … X bars"
//   complete    → toast fires elsewhere; this card un-mounts via clearJob
//   error       → toast fires elsewhere; this card un-mounts via clearJob

import { useState } from "react";
import { useJobStream, type JobStreamObservation } from "./use-job-stream";
import type { FeedJobKey } from "@/lib/stores/data-jobs-store";
import type { SseProgressPayload, SseQueuedPayload } from "@/types/data-tab";

interface Props {
  jobKey: FeedJobKey;
  exchange: string;
  /** Display hint (the FE-composed outcome feed-id like `EqV_1m_1k`). */
  outcomeHint: string;
}

export function JobProgressCard({ jobKey, exchange, outcomeHint }: Props) {
  const [obs, setObs] = useState<JobStreamObservation>({ latest: null, type: null });
  useJobStream(jobKey, exchange, setObs);

  let line = "Connecting…";
  if (obs.type === "queued") {
    const q = obs.latest as SseQueuedPayload;
    line = `Queued (#${q.queue_position})`;
  } else if (obs.type === "started") {
    line = "Aggregating…";
  } else if (obs.type === "progress") {
    const p = obs.latest as SseProgressPayload;
    const partition = p.current_partition ?? "—";
    line = `Aggregating ${partition} … ${p.bars_emitted.toLocaleString()} bars`;
  }

  return (
    <div className="border border-border-subtle rounded px-3 py-2 bg-bg-surface flex items-center justify-between text-sm">
      <span className="font-mono text-text-primary truncate" title={outcomeHint}>
        {outcomeHint}
      </span>
      <span className="text-text-secondary ml-3">{line}</span>
    </div>
  );
}
