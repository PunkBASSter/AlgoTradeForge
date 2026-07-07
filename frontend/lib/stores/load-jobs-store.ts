// Persistent load-job tracking for archive backfill jobs. Keyed by job_id; label is the
// human-readable `${symbol} ${feedName}` hint shown while the snapshot is loading.

import { create } from "zustand";
import { persist } from "zustand/middleware";

export interface LoadJobEntry {
  jobId: string;
  label: string;
}

interface LoadJobsStore {
  jobs: Record<string, LoadJobEntry>;
  addJob: (jobId: string, label: string) => void;
  removeJob: (jobId: string) => void;
}

export const useLoadJobsStore = create<LoadJobsStore>()(
  persist(
    (set) => ({
      jobs: {},
      addJob: (jobId, label) =>
        set((s) => ({
          jobs: { ...s.jobs, [jobId]: { jobId, label } },
        })),
      removeJob: (jobId) =>
        set((s) => {
          const next = { ...s.jobs };
          delete next[jobId];
          return { jobs: next };
        }),
    }),
    {
      name: "atf-load-jobs",
      partialize: (s) => ({ jobs: s.jobs }),
    },
  ),
);
