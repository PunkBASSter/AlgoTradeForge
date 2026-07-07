"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { findMissingMonths, loadRangeForMonths } from "@/lib/data/coverage";
import { useLoadJobsStore } from "@/lib/stores/load-jobs-store";
import { useLoadJob } from "@/hooks/use-load-job";
import type { DataFeedSubscription, TimeBarSubscription } from "@/types/api";

interface Props {
  primaries: DataFeedSubscription[];
  startTime: string | null;
  endTime: string | null;
}

interface RowProps {
  sub: TimeBarSubscription;
  startTime: string;
  endTime: string;
}

function CoverageRow({ sub, startTime, endTime }: RowProps) {
  const [jobId, setJobId] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const addJob = useLoadJobsStore((s) => s.addJob);

  const assetsQuery = useQuery({
    queryKey: ["data", "assets"],
    queryFn: ({ signal }) => dataApi.getAssets(signal),
    staleTime: Infinity,
  });

  const catalogEntry =
    assetsQuery.data?.assets.find(
      (a) => a.exchange === sub.exchange && a.symbol === sub.assetName,
    ) ?? null;

  const coverageQuery = useQuery({
    queryKey: [
      "data",
      "coverage",
      catalogEntry?.exchange,
      catalogEntry?.display_name,
      catalogEntry?.type,
    ],
    queryFn: ({ signal }) =>
      dataApi.getCoverage(
        catalogEntry!.exchange,
        catalogEntry!.display_name,
        catalogEntry!.type,
        signal,
      ),
    enabled: !!catalogEntry,
    staleTime: 30_000,
  });

  const jobQuery = useLoadJob(jobId);
  const isRunning =
    jobQuery.data?.state === "queued" || jobQuery.data?.state === "running";

  if (!catalogEntry) return null;
  if (!coverageQuery.data) return null;

  const entry = coverageQuery.data.feeds.find(
    (e) => e.feed_name === "candles" && e.interval === sub.timeFrame,
  );
  const missing = findMissingMonths(entry?.covered_months ?? [], startTime, endTime);

  if (missing.length === 0) return null;

  const rangeLabel =
    missing.length === 1
      ? missing[0]
      : `${missing[0]} … ${missing[missing.length - 1]}`;

  async function handleLoad() {
    if (!catalogEntry || missing.length === 0 || loading || isRunning) return;
    setLoading(true);
    try {
      const range = loadRangeForMonths(missing);
      const resp = await dataApi.postLoad({
        exchange: catalogEntry.exchange,
        symbol: catalogEntry.display_name,
        asset_type: catalogEntry.type,
        feed_name: "candles",
        interval: sub.timeFrame,
        ...range,
      });
      addJob(resp.job_id, `${catalogEntry.display_name} candles`);
      setJobId(resp.job_id);
    } catch (err) {
      if (err instanceof DataApiError && err.status === 409) {
        const activeJobId = (err.body as { active_job_id: string }).active_job_id;
        addJob(activeJobId, `${catalogEntry.display_name} candles`);
        setJobId(activeJobId);
      }
    } finally {
      setLoading(false);
    }
  }

  return (
    <div
      role="alert"
      className="flex items-center gap-3 rounded border border-accent-yellow/50 bg-accent-yellow/10 px-3 py-2 text-sm text-accent-yellow"
    >
      <span className="flex-1">
        {`Candles ${sub.timeFrame} for ${catalogEntry.display_name}: ${missing.length} archived month${missing.length !== 1 ? "s" : ""} missing in the selected range (${rangeLabel})`}
        {isRunning && jobQuery.data && (
          <span className="ml-2 text-xs opacity-80">
            {jobQuery.data.months_done}/{jobQuery.data.months_total}
          </span>
        )}
      </span>
      <button
        type="button"
        onClick={() => {
          void handleLoad();
        }}
        disabled={loading || isRunning}
        className="shrink-0 rounded bg-accent-yellow/20 px-3 py-1 text-xs font-medium hover:bg-accent-yellow/30 disabled:cursor-not-allowed disabled:opacity-50"
      >
        Load
      </button>
    </div>
  );
}

export function CoverageHint({ primaries, startTime, endTime }: Props) {
  if (!startTime || !endTime) return null;

  const timeBars = primaries.filter(
    (p): p is TimeBarSubscription => p.kind === "TimeBar",
  );
  if (timeBars.length === 0) return null;

  return (
    <div className="space-y-1">
      {timeBars.map((p) => (
        <CoverageRow
          key={`${p.exchange}|${p.assetName}|${p.timeFrame}`}
          sub={p}
          startTime={startTime}
          endTime={endTime}
        />
      ))}
    </div>
  );
}
