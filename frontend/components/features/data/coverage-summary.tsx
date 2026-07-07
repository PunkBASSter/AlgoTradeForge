"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { findMissingMonths, loadRangeForMonths } from "@/lib/data/coverage";
import { exchangeSymbolOf } from "@/lib/data/coverage-mapping";
import { useLoadJobsStore } from "@/lib/stores/load-jobs-store";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import type { AssetCatalogEntry, CoverageFeedEntry } from "@/types/data-tab";

interface CoverageSummaryProps {
  exchange: string;
  asset: AssetCatalogEntry;
  /** Non-null result from mapCatalogFeedToCoverage. interval: "" signals Side-mode matching. */
  mapping: { feedName: string; interval: string };
}

function findEntry(
  feeds: CoverageFeedEntry[],
  mapping: { feedName: string; interval: string },
): CoverageFeedEntry | undefined {
  if (mapping.interval !== "") {
    return feeds.find(
      (e) => e.feed_name === mapping.feedName && e.interval === mapping.interval,
    );
  }
  // Side-mode: match by feed_name alone OR by the combined feed_name_interval form.
  return feeds.find(
    (e) =>
      e.feed_name === mapping.feedName ||
      `${e.feed_name}_${e.interval}` === mapping.feedName,
  );
}

export function CoverageSummary({ exchange, asset, mapping }: CoverageSummaryProps) {
  const { toast } = useToast();
  const addJob = useLoadJobsStore((s) => s.addJob);

  const coverage = useQuery({
    queryKey: ["data", "coverage", exchange, exchangeSymbolOf(asset), asset.type],
    queryFn: ({ signal }) =>
      dataApi.getCoverage(exchange, exchangeSymbolOf(asset), asset.type, signal),
    staleTime: 30_000,
  });

  const loadMutation = useMutation({
    mutationFn: (body: Parameters<typeof dataApi.postLoad>[0]) => dataApi.postLoad(body),
    onSuccess: (resp, body) => {
      addJob(resp.job_id, `${asset.display_name} ${body.feed_name}`);
      toast(`Load started (${resp.job_id.slice(0, 8)})`, "success");
    },
    onError: (err, body) => {
      if (err instanceof DataApiError && err.status === 409) {
        const activeJobId = (err.body as { active_job_id: string }).active_job_id;
        addJob(activeJobId, `${asset.display_name} ${body.feed_name}`);
        toast("Already running — attached", "info");
        return;
      }
      toast(err instanceof Error ? err.message : String(err), "error");
    },
  });

  if (coverage.isLoading) {
    return <div className="text-text-secondary text-xs">Loading coverage…</div>;
  }
  if (coverage.error || !coverage.data) return null;

  const entry = findEntry(coverage.data.feeds, mapping);
  if (!entry) return null;

  const n = entry.covered_months.length;
  const hasTimestamps =
    entry.first_timestamp !== null && entry.last_timestamp !== null;

  const firstCovered = entry.covered_months[0];
  const lastCovered = entry.covered_months[n - 1];
  const rangeLabel =
    hasTimestamps && n > 0 ? ` (${firstCovered} – ${lastCovered})` : "";

  const missing: string[] = hasTimestamps
    ? findMissingMonths(
        entry.covered_months,
        new Date(entry.first_timestamp!).toISOString(),
        new Date(entry.last_timestamp!).toISOString(),
      )
    : [];

  function handleLoadMissing() {
    if (!entry) return;
    const range = loadRangeForMonths(missing);
    loadMutation.mutate({
      exchange,
      symbol: exchangeSymbolOf(asset),
      asset_type: asset.type,
      feed_name: entry.feed_name,
      interval: entry.interval,
      from: range.from,
      to: range.to,
    });
  }

  return (
    <div className="space-y-2">
      <div className="text-xs text-text-secondary">
        Archive coverage: {n} month{n !== 1 ? "s" : ""}
        {rangeLabel}
      </div>

      {missing.length > 0 && (
        <div
          role="alert"
          className="border border-accent-yellow/50 bg-accent-yellow/10 text-accent-yellow px-3 py-2 rounded text-sm space-y-2"
        >
          <div>
            {missing.length} archived month{missing.length !== 1 ? "s" : ""} missing:{" "}
            {missing.join(", ")}
          </div>
          <Button
            type="button"
            variant="primary"
            onClick={handleLoadMissing}
            disabled={loadMutation.isPending}
          >
            {loadMutation.isPending ? "Loading…" : "Load missing months"}
          </Button>
        </div>
      )}
    </div>
  );
}
