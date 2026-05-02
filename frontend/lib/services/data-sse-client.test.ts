import { describe, it, expect, vi, beforeEach } from "vitest";
import { connectProgress, GoneError } from "./data-sse-client";

// Mock @microsoft/fetch-event-source so we can drive its callbacks deterministically.
// This isolates the client's contract (header injection, terminal event handling, abort
// propagation) from the network.

interface FetchEventSourceCall {
  url: string;
  init: {
    method?: string;
    headers?: Record<string, string>;
    signal?: AbortSignal;
    onopen?: (resp: Response) => Promise<void>;
    onmessage?: (msg: { id: string; event: string; data: string }) => void;
    onerror?: (err: unknown) => void;
    onclose?: () => void;
  };
}

const captured: FetchEventSourceCall[] = [];

vi.mock("@microsoft/fetch-event-source", () => ({
  fetchEventSource: vi.fn((url: string, init: FetchEventSourceCall["init"]) => {
    captured.push({ url, init });
    // Default: never-resolving promise — tests that need control complete it manually.
    return new Promise<void>(() => {});
  }),
}));

beforeEach(() => {
  captured.length = 0;
});

describe("connectProgress", () => {
  it("forwards Last-Event-ID header when lastEventId is provided", () => {
    const ctrl = new AbortController();
    void connectProgress({
      jobId: "abc",
      lastEventId: 42,
      signal: ctrl.signal,
      handlers: { onEvent: () => {}, onError: () => {}, onClose: () => {} },
    });
    expect(captured).toHaveLength(1);
    expect(captured[0].init.headers).toEqual({ "Last-Event-ID": "42" });
  });

  it("omits Last-Event-ID header when lastEventId is undefined (first connect)", () => {
    const ctrl = new AbortController();
    void connectProgress({
      jobId: "abc",
      signal: ctrl.signal,
      handlers: { onEvent: () => {}, onError: () => {}, onClose: () => {} },
    });
    expect(captured[0].init.headers).toEqual({});
  });

  it("hits /api/data/aggregations/{jobId}/progress on the proxy URL", () => {
    void connectProgress({
      jobId: "job-12-abc",
      signal: new AbortController().signal,
      handlers: { onEvent: () => {}, onError: () => {}, onClose: () => {} },
    });
    expect(captured[0].url).toMatch(/\/api\/data\/aggregations\/job-12-abc\/progress$/);
  });

  it("dispatches parsed event payloads to onEvent with id + type", () => {
    const onEvent = vi.fn();
    void connectProgress({
      jobId: "j1",
      signal: new AbortController().signal,
      handlers: { onEvent, onError: () => {}, onClose: () => {} },
    });

    captured[0].init.onmessage!({
      id: "1",
      event: "progress",
      data: JSON.stringify({ job_id: "j1", current_partition: "2024-01", bars_emitted: 42, elapsed_ms: 100 }),
    });

    expect(onEvent).toHaveBeenCalledOnce();
    const [id, type, data] = onEvent.mock.calls[0];
    expect(id).toBe(1);
    expect(type).toBe("progress");
    expect(data).toMatchObject({ bars_emitted: 42 });
  });

  it("ignores frames with unknown event types (resilient to upstream evolution)", () => {
    const onEvent = vi.fn();
    void connectProgress({
      jobId: "j1",
      signal: new AbortController().signal,
      handlers: { onEvent, onError: () => {}, onClose: () => {} },
    });

    captured[0].init.onmessage!({ id: "1", event: "ping", data: "{}" });
    expect(onEvent).not.toHaveBeenCalled();
  });

  it("ignores frames with malformed JSON (keeps stream alive)", () => {
    const onEvent = vi.fn();
    void connectProgress({
      jobId: "j1",
      signal: new AbortController().signal,
      handlers: { onEvent, onError: () => {}, onClose: () => {} },
    });

    captured[0].init.onmessage!({ id: "1", event: "progress", data: "not json{{" });
    expect(onEvent).not.toHaveBeenCalled();
  });

  it("ignores frames with non-numeric ids", () => {
    const onEvent = vi.fn();
    void connectProgress({
      jobId: "j1",
      signal: new AbortController().signal,
      handlers: { onEvent, onError: () => {}, onClose: () => {} },
    });

    captured[0].init.onmessage!({ id: "abc", event: "progress", data: "{}" });
    expect(onEvent).not.toHaveBeenCalled();
  });

  it("throws GoneError from onopen when upstream returns 410", async () => {
    void connectProgress({
      jobId: "expired",
      signal: new AbortController().signal,
      handlers: { onEvent: () => {}, onError: () => {}, onClose: () => {} },
    });

    const onopen = captured[0].init.onopen!;
    await expect(onopen(new Response("", { status: 410 }))).rejects.toThrow(GoneError);
  });
});
