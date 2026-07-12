// SSE resume cursor for the unified Jobs panel. Holds ONLY the last-seen event id per
// jobId so a page reload can resume the /jobs/{id}/progress stream via Last-Event-ID.
// Job identity + progress live on the server (useJobs) — this store is not authoritative
// for anything the user sees.

import { create } from "zustand";
import { persist } from "zustand/middleware";

export interface JobCursor {
  lastEventId: string;
  /** Epoch-ms; used to purge stale cursors (server job retention is far shorter). */
  updatedAt: number;
}

interface JobsStore {
  cursors: Record<string, JobCursor>;
  recordEvent: (jobId: string, id: string) => void;
  /** Drops cursors older than `maxAgeMs` (default 24h). Called once on rehydrate. */
  purgeStale: (maxAgeMs?: number) => void;
}

// 24h: server retention is minutes, so anything older will 410 on resume anyway.
const STALE_THRESHOLD_MS = 24 * 60 * 60 * 1000;
const STORAGE_VERSION = 1;

export const useJobsStore = create<JobsStore>()(
  persist(
    (set) => ({
      cursors: {},
      recordEvent: (jobId, id) =>
        set((s) => ({
          cursors: { ...s.cursors, [jobId]: { lastEventId: id, updatedAt: Date.now() } },
        })),
      purgeStale: (maxAgeMs = STALE_THRESHOLD_MS) =>
        set((s) => {
          const cutoff = Date.now() - maxAgeMs;
          const kept: Record<string, JobCursor> = {};
          for (const [k, v] of Object.entries(s.cursors)) {
            if (v.updatedAt >= cutoff) kept[k] = v;
          }
          return { cursors: kept };
        }),
    }),
    {
      name: "atf-jobs-cursor",
      version: STORAGE_VERSION,
      partialize: (s) => ({ cursors: s.cursors }),
      onRehydrateStorage: () => (state) => {
        state?.purgeStale(STALE_THRESHOLD_MS);
      },
    },
  ),
);
