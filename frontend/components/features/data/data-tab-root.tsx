"use client";

import { useState } from "react";
import { useIsFetching, useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import { ExchangeCard } from "./exchange-card";
import { DataSidebar } from "./data-sidebar";
import { JobCard } from "./job-card";
import { GroupsPanel } from "./groups/groups-panel";
import { useJobs } from "@/hooks/use-jobs";

type DataZone = "explorer" | "groups";

// Server-hydrated list of all in-flight/terminal jobs of every kind. Identity + progress
// come from the polled useJobs() list; only the SSE resume cursor lives client-side.
function JobsPanel() {
  const { data } = useJobs();
  const jobs = data ?? [];
  if (jobs.length === 0) return null;
  return (
    <section className="space-y-1 mb-3" aria-label="Jobs">
      <div className="text-xs text-text-muted uppercase tracking-wide">Jobs</div>
      {jobs.map((job) => (
        <JobCard key={job.job_id} job={job} />
      ))}
    </section>
  );
}

export function DataTabRoot() {
  const [zone, setZone] = useState<DataZone>("explorer");

  const { data, isLoading, error } = useQuery({
    queryKey: ["data", "exchanges"],
    queryFn: ({ signal }) => dataApi.getExchanges(signal),
    enabled: zone === "explorer",
  });

  // Block grid clicks while a cell's response is in flight to prevent rapid-click
  // request supersession (each new click aborts the prior request mid-read).
  const pendingCellQueries = useIsFetching({
    predicate: (q) =>
      q.queryKey[0] === "data" &&
      (q.queryKey[1] === "feed-status" || q.queryKey[1] === "aggregation-options"),
  });
  const cellsBusy = pendingCellQueries > 0;

  return (
    <div className={`flex h-full flex-col ${cellsBusy ? "cursor-wait" : ""}`}>
      {/* Zone tab header */}
      <div className="flex items-center gap-1 border-b border-border-default px-6 pt-4 pb-0 shrink-0">
        <button
          type="button"
          onClick={() => setZone("explorer")}
          className={`px-4 py-2 text-sm font-medium border-b-2 transition-colors ${
            zone === "explorer"
              ? "border-accent-blue text-text-primary"
              : "border-transparent text-text-muted hover:text-text-secondary"
          }`}
        >
          Explorer
        </button>
        <button
          type="button"
          onClick={() => setZone("groups")}
          className={`px-4 py-2 text-sm font-medium border-b-2 transition-colors ${
            zone === "groups"
              ? "border-accent-blue text-text-primary"
              : "border-transparent text-text-muted hover:text-text-secondary"
          }`}
        >
          Groups
        </button>
      </div>

      {zone === "groups" && (
        <div className="flex-1 overflow-auto">
          <GroupsPanel />
        </div>
      )}

      {zone === "explorer" && (
        <div className={`flex flex-1 overflow-hidden ${cellsBusy ? "pointer-events-none" : ""}`}>
          <main className="flex-1 overflow-auto p-6 space-y-2">
            <h1 className="text-2xl font-semibold text-text-primary mb-4">Data</h1>

            <JobsPanel />

            {isLoading && (
              <div className="text-text-secondary text-sm">Loading exchanges…</div>
            )}

            {error && (
              <div className="text-accent-red text-sm">
                Failed to load exchanges:{" "}
                {error instanceof Error ? error.message : String(error)}
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
          <DataSidebar />
        </div>
      )}
    </div>
  );
}
