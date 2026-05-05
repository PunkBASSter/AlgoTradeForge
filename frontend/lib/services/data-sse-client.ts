// SSE client wrapping @microsoft/fetch-event-source. Native EventSource doesn't support
// custom headers, but we need to inject `Last-Event-ID` from localStorage on first
// connect for resume. fetch-event-source also propagates AbortController cleanly.

import { fetchEventSource } from "@microsoft/fetch-event-source";
import type {
  SseEventEnvelope,
  SseEventType,
  SseQueuedPayload,
  SseStartedPayload,
  SseProgressPayload,
  SseCompletePayload,
  SseErrorPayload,
  SseCancelledPayload,
} from "@/types/data-tab";

const BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

export interface ProgressStreamHandlers {
  /**
   * Per-event callback. `id` is the upstream's monotonic seq for resume; persist it.
   * `env` is a discriminated union — narrow `env.data` via `if (env.type === "...")`.
   */
  onEvent: (id: number, env: SseEventEnvelope) => void;
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

const TERMINAL_EVENTS: ReadonlySet<SseEventType> = new Set(["complete", "error", "cancelled"]);

/**
 * Subscribes to /api/data/aggregations/{jobId}/progress. Resolves when the stream
 * closes; rejects on unrecoverable errors. When `lastEventId` is provided, the upstream
 * replays events past that id. 410 Gone surfaces as a tagged GoneError so the caller
 * can clearJob and stop reconnecting.
 */
export async function connectProgress(opts: ConnectProgressOptions): Promise<void> {
  const { jobId, lastEventId, signal, handlers } = opts;

  const headers: Record<string, string> = {};
  if (lastEventId !== undefined) headers["Last-Event-ID"] = String(lastEventId);

  await fetchEventSource(
    `${BASE_URL}/api/data/aggregations/${encodeURIComponent(jobId)}/progress`,
    {
      method: "GET",
      headers,
      signal,
      // We manage reconnect at the hook level via persisted jobId + lastEventId;
      // fetch-event-source's built-in retry would race the store.
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
      },
      onmessage: (msg) => {
        // Defensive: skip unrecognized event types rather than throwing — keeps long
        // streams resilient if the server adds new events.
        const type = (msg.event ?? "message") as SseEventType;
        if (!isKnownEventType(type)) return;
        const id = Number(msg.id);
        if (!Number.isFinite(id)) return;

        let env: SseEventEnvelope;
        try {
          switch (type) {
            case "queued":
              env = { type, data: JSON.parse(msg.data) as SseQueuedPayload };
              break;
            case "started":
              env = { type, data: JSON.parse(msg.data) as SseStartedPayload };
              break;
            case "progress":
              env = { type, data: JSON.parse(msg.data) as SseProgressPayload };
              break;
            case "complete":
              env = { type, data: JSON.parse(msg.data) as SseCompletePayload };
              break;
            case "error":
              env = { type, data: JSON.parse(msg.data) as SseErrorPayload };
              break;
            case "cancelled":
              env = { type, data: JSON.parse(msg.data) as SseCancelledPayload };
              break;
          }
        } catch {
          return;   // malformed payload — skip frame, keep stream alive
        }

        handlers.onEvent(id, env);

        // Throw AFTER onEvent so the consumer sees the terminal payload; throwing makes
        // fetch-event-source close cleanly without retry.
        if (TERMINAL_EVENTS.has(type)) {
          throw new TerminalEventError();
        }
      },
      onerror: (err) => {
        // Bubble up; the default retry-loop would otherwise pin us forever.
        throw err;
      },
      onclose: () => {},
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
  return s === "queued" || s === "started" || s === "progress"
    || s === "complete" || s === "error" || s === "cancelled";
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
