// Phase 3 — SSE client wrapping @microsoft/fetch-event-source. Native EventSource doesn't
// support custom headers, but we need to inject `Last-Event-ID` from localStorage on first
// connect (P3-18 resume). fetch-event-source also propagates AbortController cleanly,
// matching the existing apiClient pattern.

import { fetchEventSource } from "@microsoft/fetch-event-source";
import type { SseEventPayload, SseEventType } from "@/types/data-tab";

const BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

export interface ProgressStreamHandlers {
  /** Per-event callback. `id` is the upstream's monotonic seq for resume; persist it. */
  onEvent: (id: number, type: SseEventType, data: SseEventPayload) => void;
  /** Network or upstream error (4xx/5xx during open, or stream interruption). */
  onError: (err: Error) => void;
  /** Stream closed normally (terminal event arrived or caller aborted). */
  onClose: () => void;
}

export interface ConnectProgressOptions {
  jobId: string;
  /** Last seen event id (from store). Forwarded as Last-Event-ID for resume. */
  lastEventId?: number;
  signal: AbortSignal;
  handlers: ProgressStreamHandlers;
}

const TERMINAL_EVENTS: ReadonlySet<SseEventType> = new Set(["complete", "error"]);

/**
 * Subscribes to /api/data/aggregations/{jobId}/progress. Returns a Promise that resolves
 * when the stream closes (either by terminal event or caller abort) and rejects on errors
 * that the handlers can't recover from.
 *
 * Resume contract: when `lastEventId` is provided, the upstream replays events past that id
 * (server-side log retention permitting). On 410 Gone (retention expired), `onError` is
 * called with a tagged error so the caller can clearJob() and stop reconnecting.
 */
export async function connectProgress(opts: ConnectProgressOptions): Promise<void> {
  const { jobId, lastEventId, signal, handlers } = opts;

  // fetchEventSource expects header values as strings.
  const headers: Record<string, string> = {};
  if (lastEventId !== undefined) headers["Last-Event-ID"] = String(lastEventId);

  await fetchEventSource(
    `${BASE_URL}/api/data/aggregations/${encodeURIComponent(jobId)}/progress`,
    {
      method: "GET",
      headers,
      signal,
      // Disable auto-retry: we manage reconnection at the hook level via the persisted
      // jobId + lastEventId. fetch-event-source's built-in retry would race with our store.
      openWhenHidden: true,
      onopen: async (response) => {
        if (response.status === 410) {
          throw new GoneError("job retention expired");
        }
        if (response.status >= 400) {
          throw new SseProtocolError(
            `SSE open failed: HTTP ${response.status} ${response.statusText}`,
          );
        }
        // 200 OK — fetch-event-source proceeds to read the body.
      },
      onmessage: (msg) => {
        // Per HTML SSE spec, `event:` defaults to "message" if absent. Our server always
        // sets it to one of {queued, started, progress, complete, error}. Defensive: skip
        // any frame we don't recognize rather than throwing — keeps long streams resilient.
        const type = (msg.event ?? "message") as SseEventType;
        if (!isKnownEventType(type)) return;
        const id = Number(msg.id);
        if (!Number.isFinite(id)) return;

        let data: SseEventPayload;
        try {
          data = JSON.parse(msg.data) as SseEventPayload;
        } catch {
          // Malformed payload — skip this frame, keep stream alive.
          return;
        }

        handlers.onEvent(id, type, data);

        // Terminal events: surface to caller via onClose by aborting the iterator. We do
        // this AFTER calling onEvent so the consumer sees the complete/error payload.
        if (TERMINAL_EVENTS.has(type)) {
          // Throwing FatalError makes fetch-event-source close cleanly without retry.
          throw new TerminalEventError();
        }
      },
      onerror: (err) => {
        // Bubble up; fetch-event-source's default retry-loop would otherwise pin us forever.
        throw err;
      },
      onclose: () => {
        // Server closed without a terminal event — treat as graceful close.
      },
    },
  ).then(
    () => handlers.onClose(),
    (err: unknown) => {
      if (err instanceof TerminalEventError) {
        handlers.onClose();
      } else if (signal.aborted) {
        handlers.onClose();
      } else {
        handlers.onError(err instanceof Error ? err : new Error(String(err)));
      }
    },
  );
}

function isKnownEventType(s: string): s is SseEventType {
  return s === "queued" || s === "started" || s === "progress" || s === "complete" || s === "error";
}

/** Thrown internally to break out of fetch-event-source on terminal events. */
class TerminalEventError extends Error {
  constructor() {
    super("terminal SSE event");
    this.name = "TerminalEventError";
  }
}

/** Surfaced to onError when the upstream returns 410 Gone (retention expired). */
export class GoneError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "GoneError";
  }
}

class SseProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SseProtocolError";
  }
}
