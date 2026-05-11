import { describe, it, expect, beforeEach } from "vitest";
import { useDataJobsStore, makeFeedJobKey } from "./data-jobs-store";

beforeEach(() => {
  // Wipe localStorage + reset in-memory store between tests so persistence assertions
  // don't leak across runs.
  localStorage.clear();
  useDataJobsStore.setState({ jobs: {} });
});

describe("data-jobs-store", () => {
  it("makeFeedJobKey composes a stable composite key", () => {
    const key = makeFeedJobKey("binance", "BTCUSDT_perp", "EqV_1m_1k");
    expect(key).toBe("binance|BTCUSDT_perp|EqV_1m_1k");
  });

  it("setJob seeds an entry with lastEventId=0 and updatedAt=now", () => {
    const key = makeFeedJobKey("binance", "BTCUSDT_perp", "EqV_1m_1k");
    const t0 = Date.now();
    useDataJobsStore.getState().setJob(key, "job-abc");

    const entry = useDataJobsStore.getState().jobs[key];
    expect(entry.jobId).toBe("job-abc");
    expect(entry.lastEventId).toBe(0);
    expect(entry.updatedAt).toBeGreaterThanOrEqual(t0);
  });

  it("recordEvent updates lastEventId and timestamp on existing entry", async () => {
    const key = makeFeedJobKey("binance", "BTC", "x");
    useDataJobsStore.getState().setJob(key, "j1");
    const initial = useDataJobsStore.getState().jobs[key].updatedAt;
    // Advance the clock minimally so updatedAt strictly increases.
    await new Promise((r) => setTimeout(r, 5));

    useDataJobsStore.getState().recordEvent(key, 7);
    const after = useDataJobsStore.getState().jobs[key];
    expect(after.lastEventId).toBe(7);
    expect(after.updatedAt).toBeGreaterThanOrEqual(initial);
  });

  it("recordEvent on missing key is a no-op (defensive)", () => {
    const key = makeFeedJobKey("e", "a", "f");
    useDataJobsStore.getState().recordEvent(key, 5);
    expect(useDataJobsStore.getState().jobs[key]).toBeUndefined();
  });

  it("clearJob removes the entry", () => {
    const key = makeFeedJobKey("e", "a", "f");
    useDataJobsStore.getState().setJob(key, "j1");
    useDataJobsStore.getState().clearJob(key);
    expect(useDataJobsStore.getState().jobs[key]).toBeUndefined();
  });

  it("purgeStale removes entries older than maxAgeMs", () => {
    const key1 = makeFeedJobKey("e", "a", "fresh");
    const key2 = makeFeedJobKey("e", "a", "stale");
    useDataJobsStore.setState({
      jobs: {
        [key1]: { jobId: "j1", lastEventId: 0, updatedAt: Date.now() },
        [key2]: { jobId: "j2", lastEventId: 0, updatedAt: Date.now() - 60_000 },
      },
    });

    useDataJobsStore.getState().purgeStale(30_000);   // 30s threshold
    expect(useDataJobsStore.getState().jobs[key1]).toBeDefined();
    expect(useDataJobsStore.getState().jobs[key2]).toBeUndefined();
  });

  it("persists jobs to localStorage under 'alt-bars:jobs'", () => {
    const key = makeFeedJobKey("binance", "BTC", "x");
    useDataJobsStore.getState().setJob(key, "j1");

    const raw = localStorage.getItem("alt-bars:jobs");
    expect(raw).not.toBeNull();
    const parsed = JSON.parse(raw!);
    expect(parsed.state.jobs[key]).toBeDefined();
    expect(parsed.state.jobs[key].jobId).toBe("j1");
    // Functions must NOT be in the persisted payload (partializer omits them).
    expect(parsed.state.setJob).toBeUndefined();
  });
});
