"use client";

import { useState } from "react";
import { useIsFetching, useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import { ExchangeCard } from "./exchange-card";
import { DataSidebar } from "./data-sidebar";
import { JobProgressCard } from "./job-progress";
import { LoadJobCard } from "./load-job-card";
import { GroupsPanel } from "./groups/groups-panel";
import { makeFeedJobKey, useDataJobsStore } from "@/lib/stores/data-jobs-store";
import { useLoadJobsStore } from "@/lib/stores/load-jobs-store";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";
import { Button } from "@/components/ui/button";

type DataZone = "explorer" | "groups";

export function DataTabRoot() {
  const [zone, setZone] = useState<DataZone>("explorer");

  const { data, isLoading, error } = useQuery({
    queryKey: ["data", "exchanges"],
    queryFn: ({ signal }) => dataApi.getExchanges(signal),
    enabled: zone === "explorer",
  });

  // Resumed from localStorage on mount; each entry mounts a JobProgressCard that
  // connects/reconnects its SSE stream.
  const activeJobs = useDataJobsStore((s) => s.jobs);
  const setJob = useDataJobsStore((s) => s.setJob);

  const loadJobs = useLoadJobsStore((s) => s.jobs);
  const removeLoadJob = useLoadJobsStore((s) => s.removeJob);

  const selection = useDataSelectionStore();

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
            <div className="flex items-center justify-between mb-4">
              <h1 className="text-2xl font-semibold text-text-primary">Data</h1>
              <Button variant="secondary" onClick={() => selection.openLoad()}>
                Load archive data
              </Button>
            </div>

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

            {Object.keys(loadJobs).length > 0 && (
              <section className="space-y-1 mb-3" aria-label="In-progress archive loads">
                <div className="text-xs text-text-muted uppercase tracking-wide">Archive loads</div>
                {Object.keys(loadJobs).map((id) => (
                  <LoadJobCard key={id} jobId={id} onDismiss={() => removeLoadJob(id)} />
                ))}
              </section>
            )}

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
          <DataSidebar
            onJobAccepted={(jobId, outcomeHint) => {
              // Key by `(exchange|asset|outcomeHint)` so a page refresh resumes the SSE
              // stream via `Last-Event-ID`.
              if (selection.exchange && selection.asset) {
                const key = makeFeedJobKey(
                  selection.exchange,
                  selection.asset.symbol,
                  outcomeHint,
                );
                setJob(key, jobId);
              }
            }}
          />
        </div>
      )}
    </div>
  );
}
