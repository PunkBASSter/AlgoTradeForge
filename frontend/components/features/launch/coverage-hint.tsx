"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { findMissingMonths, loadRangeForMonths } from "@/lib/data/coverage";
import { exchangeSymbolOf } from "@/lib/data/coverage-mapping";
import { useToast } from "@/components/ui/toast";
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
  const [loading, setLoading] = useState(false);
  const { toast } = useToast();

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
      catalogEntry ? exchangeSymbolOf(catalogEntry) : undefined,
      catalogEntry?.type,
    ],
    queryFn: ({ signal }) =>
      dataApi.getCoverage(
        catalogEntry!.exchange,
        exchangeSymbolOf(catalogEntry!),
        catalogEntry!.type,
        signal,
      ),
    enabled: !!catalogEntry,
    staleTime: 30_000,
  });

  if (!catalogEntry) return null;
  if (!coverageQuery.data) return null;

  const entry = coverageQuery.data.feeds.find(
    (e) => e.feed_name === "candles" && e.interval === sub.timeFrame,
  );
  const effectiveFrom =
    entry?.first_timestamp != null &&
    startTime < new Date(entry.first_timestamp).toISOString()
      ? new Date(entry.first_timestamp).toISOString()
      : startTime;
  const missing = findMissingMonths(entry?.covered_months ?? [], effectiveFrom, endTime);

  if (missing.length === 0) return null;

  const rangeLabel =
    missing.length === 1
      ? missing[0]
      : `${missing[0]} … ${missing[missing.length - 1]}`;

  async function handleLoad() {
    if (!catalogEntry || missing.length === 0 || loading) return;
    setLoading(true);
    try {
      const range = loadRangeForMonths(missing);
      await dataApi.postLoad({
        exchange: catalogEntry.exchange,
        symbol: exchangeSymbolOf(catalogEntry),
        asset_type: catalogEntry.type,
        feed_name: "candles",
        interval: sub.timeFrame,
        ...range,
      });
      toast(`Load started for ${catalogEntry.display_name} candles — see Jobs panel`, "success");
    } catch (err) {
      if (err instanceof DataApiError && err.status === 409) {
        toast("Already loading — see Jobs panel", "info");
      } else {
        toast(err instanceof Error ? err.message : String(err), "error");
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
      </span>
      <button
        type="button"
        onClick={() => {
          void handleLoad();
        }}
        disabled={loading}
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
