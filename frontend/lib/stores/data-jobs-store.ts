// Persistent jobId tracking for SSE resume across page reloads. Composite key
// `(exchange|asset|outcomeFeedIdHint)` lets one asset have multiple in-flight jobs
// without collision; the FE-composed outcome hint dedups before the server returns its
// canonical outcome id.

import { create } from "zustand";
import { persist } from "zustand/middleware";

export type FeedJobKey = `${string}|${string}|${string}`;

export function makeFeedJobKey(exchange: string, asset: string, feedIdHint: string): FeedJobKey {
  return `${exchange}|${asset}|${feedIdHint}` as FeedJobKey;
}

export interface JobEntry {
  jobId: string;
  lastEventId: number;
  /** Epoch-ms; used to purge stale entries (job retention is ~10min server-side). */
  updatedAt: number;
}

interface DataJobsStore {
  jobs: Record<FeedJobKey, JobEntry>;
  setJob: (key: FeedJobKey, jobId: string) => void;
  recordEvent: (key: FeedJobKey, eventId: number) => void;
  clearJob: (key: FeedJobKey) => void;
  /** Purges entries older than `maxAgeMs`. Called once on hydrate. */
  purgeStale: (maxAgeMs: number) => void;
}

// 24h: server retention is ~10m, so anything older will 410 on resume anyway.
const STALE_THRESHOLD_MS = 24 * 60 * 60 * 1000;
const STORAGE_VERSION = 1;

export const useDataJobsStore = create<DataJobsStore>()(
  persist(
    (set) => ({
      jobs: {},
      setJob: (key, jobId) =>
        set((s) => ({
          jobs: {
            ...s.jobs,
            [key]: { jobId, lastEventId: 0, updatedAt: Date.now() },
          },
        })),
      recordEvent: (key, eventId) =>
        set((s) => {
          const entry = s.jobs[key];
          if (!entry) return s;
          return {
            jobs: {
              ...s.jobs,
              [key]: { ...entry, lastEventId: eventId, updatedAt: Date.now() },
            },
          };
        }),
      clearJob: (key) =>
        set((s) => {
          // New object reference so persistence middleware sees a change.
          const next = { ...s.jobs };
          delete next[key];
          return { jobs: next };
        }),
      purgeStale: (maxAgeMs) =>
        set((s) => {
          const cutoff = Date.now() - maxAgeMs;
          const kept: Record<FeedJobKey, JobEntry> = {};
          for (const [k, v] of Object.entries(s.jobs)) {
            if (v.updatedAt >= cutoff) kept[k as FeedJobKey] = v;
          }
          return { jobs: kept };
        }),
    }),
    {
      name: "alt-bars:jobs",
      version: STORAGE_VERSION,
      partialize: (s) => ({ jobs: s.jobs }),
      onRehydrateStorage: () => (state) => {
        state?.purgeStale(STALE_THRESHOLD_MS);
      },
    },
  ),
);
