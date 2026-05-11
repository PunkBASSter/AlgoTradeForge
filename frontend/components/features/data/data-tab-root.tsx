"use client";

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

  // Resumed from localStorage on mount; each entry mounts a JobProgressCard that
  // connects/reconnects its SSE stream.
  const activeJobs = useDataJobsStore((s) => s.jobs);
  const setJob = useDataJobsStore((s) => s.setJob);

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
          // Key by `(exchange|asset|outcomeHint)` so a page refresh resumes the SSE
          // stream via `Last-Event-ID`.
          if (selection.exchange && selection.asset) {
            const key = makeFeedJobKey(selection.exchange, selection.asset.symbol, outcomeHint);
            setJob(key, jobId);
          }
        }}
      />
    </div>
  );
}
