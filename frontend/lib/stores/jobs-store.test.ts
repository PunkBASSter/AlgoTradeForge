import { describe, it, expect, beforeEach, vi, afterEach } from "vitest";
import { useJobsStore } from "./jobs-store";

beforeEach(() => {
  localStorage.clear();
  useJobsStore.setState({ cursors: {} });
});

afterEach(() => {
  vi.useRealTimers();
});

describe("jobs-store", () => {
  it("recordEvent inserts a cursor for a new jobId with the given lastEventId and updatedAt", () => {
    const t0 = Date.now();
    useJobsStore.getState().recordEvent("job-1", "evt-42");

    const cursor = useJobsStore.getState().cursors["job-1"];
    expect(cursor.lastEventId).toBe("evt-42");
    expect(cursor.updatedAt).toBeGreaterThanOrEqual(t0);
  });

  it("recordEvent overwrites lastEventId and updatedAt on a repeat call for the same jobId", () => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000_000);

    useJobsStore.getState().recordEvent("job-1", "evt-first");
    expect(useJobsStore.getState().cursors["job-1"].lastEventId).toBe("evt-first");
    expect(useJobsStore.getState().cursors["job-1"].updatedAt).toBe(1_000_000);

    vi.setSystemTime(2_000_000);
    useJobsStore.getState().recordEvent("job-1", "evt-later");

    const cursor = useJobsStore.getState().cursors["job-1"];
    expect(cursor.lastEventId).toBe("evt-later");
    expect(cursor.updatedAt).toBe(2_000_000);
  });

  it("purgeStale drops entries older than maxAgeMs and keeps fresh ones", () => {
    const now = Date.now();
    useJobsStore.setState({
      cursors: {
        "fresh-job": { lastEventId: "f1", updatedAt: now },
        "stale-job": { lastEventId: "s1", updatedAt: now - 60_000 },
      },
    });

    useJobsStore.getState().purgeStale(30_000); // 30s threshold; stale-job is 60s old

    expect(useJobsStore.getState().cursors["fresh-job"]).toBeDefined();
    expect(useJobsStore.getState().cursors["stale-job"]).toBeUndefined();
  });

  it("persisted payload under 'atf-jobs-cursor' contains only cursors — no function fields", () => {
    useJobsStore.getState().recordEvent("job-xyz", "evt-1");

    const raw = localStorage.getItem("atf-jobs-cursor");
    expect(raw).not.toBeNull();
    const parsed = JSON.parse(raw!);
    expect(parsed.state.cursors["job-xyz"]).toBeDefined();
    expect(parsed.state.cursors["job-xyz"].lastEventId).toBe("evt-1");
    // partialize must omit actions — no function fields in the persisted shape
    expect(parsed.state.recordEvent).toBeUndefined();
    expect(parsed.state.purgeStale).toBeUndefined();
  });
});
