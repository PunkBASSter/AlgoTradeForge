"use client";

// Phase 3 — Data tab top-level client component (P3-11). Lists exchanges with
// expandable per-exchange cards. The asset×feed grid (P3-12/13/14) renders inside
// each expanded card; the right sidebar (P3-15/16/19) hosts the Status + new-aggregate
// cards once a cell is selected. In-flight aggregation jobs (P3-17/18) render as
// floating progress cards above the exchange list and resume from localStorage on
// page reload.

import { useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import { ExchangeCard } from "./exchange-card";
import { DataSidebar } from "./data-sidebar";
import { JobProgressCard } from "./job-progress";
import { makeFeedJobKey, useDataJobsStore } from "@/lib/stores/data-jobs-store";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";

export function DataTabRoot() {
  const { data, isLoading, error } = useQuery({
    queryKey: ["data", "exchanges"],
    queryFn: ({ signal }) => dataApi.getExchanges(signal),
  });

  // Active jobs (resumed from localStorage on mount). Each non-cleared entry mounts a
  // JobProgressCard which connects/reconnects an SSE stream via `useJobStream`.
  const activeJobs = useDataJobsStore((s) => s.jobs);
  const setJob = useDataJobsStore((s) => s.setJob);

  // Sidebar selection state — used to thread the FE-composed outcome hint from the
  // form's submit-success callback into the persistent jobs store.
  const selection = useDataSelectionStore();

  return (
    <div className="flex h-full">
      <main className="flex-1 overflow-auto p-6 space-y-2">
        <h1 className="text-2xl font-semibold text-text-primary mb-4">Data</h1>

        {Object.keys(activeJobs).length > 0 && (
          <section className="space-y-1 mb-3" aria-label="In-flight aggregations">
            <div className="text-xs text-text-muted uppercase tracking-wide">In progress</div>
            {Object.entries(activeJobs).map(([key, _entry]) => {
              const [exchange, , outcomeHint] = key.split("|");
              return (
                <JobProgressCard
                  key={key}
                  jobKey={key as ReturnType<typeof makeFeedJobKey>}
                  exchange={exchange}
                  outcomeHint={outcomeHint}
                />
              );
            })}
          </section>
        )}

        {isLoading && (
          <div className="text-text-secondary text-sm">Loading exchanges…</div>
        )}

        {error && (
          <div className="text-accent-red text-sm">
            Failed to load exchanges: {error instanceof Error ? error.message : String(error)}
          </div>
        )}

        {data?.exchanges.map((e) => (
          <ExchangeCard key={e.name} exchange={e} />
        ))}

        {data && data.exchanges.length === 0 && (
          <div className="text-text-secondary text-sm">
            No exchanges configured. Run the HistoryLoader to register data.
          </div>
        )}
      </main>
      <DataSidebar
        onJobAccepted={(jobId, outcomeHint) => {
          // Persist the jobId keyed by `(exchange|asset|outcomeHint)` so a page refresh
          // resumes the SSE stream via `Last-Event-ID` (P3-18). The active selection
          // identifies which exchange + asset this job belongs to.
          if (selection.exchange && selection.asset) {
            const key = makeFeedJobKey(selection.exchange, selection.asset.symbol, outcomeHint);
            setJob(key, jobId);
          }
        }}
      />
    </div>
  );
}
