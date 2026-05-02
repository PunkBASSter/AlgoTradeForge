// Phase 3 — persistent jobId tracking for SSE resume (P3-18). Stores
// `(exchange|asset|feedIdHint) -> { jobId, lastEventId, updatedAt }` in localStorage.
//
// Why persist: a user submits a long aggregate, refreshes the page, and expects to see
// the in-flight progress card immediately. Without persistence, the FE would lose the
// jobId and the SSE stream would never reconnect.
//
// Why composite key: an asset can have multiple in-flight jobs (one per outcome feed).
// Keying by `(exchange|asset|outcomeFeedIdHint)` avoids overwriting one job's state with
// another's. The `outcomeFeedIdHint` is composed FE-side from typeCode + sourceFeedId +
// thresholdInput so we can dedup before the server returns its canonical outcome id.

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

const STALE_THRESHOLD_MS = 24 * 60 * 60 * 1000;   // 24h — server retention is ~10m, so 24h is generous
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
          // Functional rather than `delete s.jobs[key]` so persistence sees a new object.
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
      // Only persist the jobs map — `setJob`/`clearJob`/etc are functions (not serializable).
      partialize: (s) => ({ jobs: s.jobs }),
      onRehydrateStorage: () => (state) => {
        // Drop entries older than 24h on hydrate (server retention is ~10min anyway, so
        // resuming past that returns 410 Gone — better to clear up front).
        state?.purgeStale(STALE_THRESHOLD_MS);
      },
    },
  ),
);
