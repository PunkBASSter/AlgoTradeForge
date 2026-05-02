"use client";

// Phase 3 — new-aggregate form (P3-16, P3-19). Source / Type / N / Aggregate.
// The Type dropdown is filtered by the eligibility-options endpoint (TRD §5.3); the
// EqI yellow-banner copy is pulled byte-identical from the same endpoint's `warnings[]`.
// N input accepts SI suffixes (k/M/G/m/u per TRD §3.4 case-sensitive).

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { isValidSi } from "@/lib/data/si-suffix";
import { pickEqiBanner } from "@/lib/data/eqi-banner";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import type { AggregateRequest, FeedCatalogEntry } from "@/types/data-tab";

interface Props {
  exchange: string;
  asset: string;
  sourceFeed: FeedCatalogEntry;
  /** Called with the upstream-assigned jobId on 202; parent persists for SSE resume. */
  onJobAccepted?: (jobId: string, outcomeFeedIdHint: string) => void;
}

export function NewAggregateForm({ exchange, asset, sourceFeed, onJobAccepted }: Props) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const [typeCode, setTypeCode] = useState<string>("");
  const [thresholdInput, setThresholdInput] = useState("");

  // Eligibility-options drives the type dropdown AND the EqI banner.
  const eligibility = useQuery({
    queryKey: ["data", "aggregation-options", exchange, asset, sourceFeed.id],
    queryFn: ({ signal }) =>
      dataApi.getAggregationOptions(exchange, asset, sourceFeed.id, signal),
  });

  const banner = eligibility.data ? pickEqiBanner(eligibility.data.warnings) : null;
  const eligibleTypes = eligibility.data?.eligible_types ?? [];

  const aggregate = useMutation({
    mutationFn: (body: AggregateRequest) => dataApi.postAggregate(exchange, asset, body),
    onSuccess: (resp) => {
      const outcomeHint = `${typeCode}_${sourceFeed.id}_${thresholdInput}`;
      onJobAccepted?.(resp.job_id, outcomeHint);
      toast(`Queued ${outcomeHint} (job ${resp.job_id.slice(0, 8)})`, "success");
      // Optimistic invalidation so the grid eventually shows the new column.
      queryClient.invalidateQueries({ queryKey: ["data", "exchange-assets", exchange] });
    },
    onError: (err) => {
      if (err instanceof DataApiError) {
        const code = err.code ?? "error";
        toast(`${code}: ${err.message}`, "error");
      } else {
        toast(err instanceof Error ? err.message : String(err), "error");
      }
    },
  });

  const canSubmit =
    !!typeCode &&
    isValidSi(thresholdInput) &&
    !aggregate.isPending &&
    eligibleTypes.includes(typeCode);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!canSubmit) return;

    // Submit using convenience-input mode: the server resolves the threshold via the
    // canonical SI grammar (TRD §3.4). We pass the original suffix string verbatim so
    // the manifest's `convenience_input` field preserves it (P0-5).
    aggregate.mutate({
      source_feed_id: sourceFeed.id,
      type_code: typeCode,
      threshold: null,
      threshold_unit: typeCode === "EqT" ? "trades" : "base_asset",
      input_mode: "convenience",
      convenience_input: thresholdInput,
      overwrite_existing: false,
    });
  };

  return (
    <form className="space-y-3" onSubmit={handleSubmit}>
      <div className="text-xs text-text-muted uppercase tracking-wide">Aggregate</div>

      <div className="text-sm">
        <div className="text-text-muted">Source</div>
        <div className="font-mono text-text-primary">{sourceFeed.id}</div>
      </div>

      {banner && (
        <div
          role="alert"
          className="border border-accent-yellow/50 bg-accent-yellow/10 text-accent-yellow px-3 py-2 rounded text-sm"
        >
          {banner}
        </div>
      )}

      <label className="block text-sm">
        <div className="text-text-muted mb-1">Type</div>
        <select
          value={typeCode}
          onChange={(e) => setTypeCode(e.target.value)}
          className="w-full bg-bg-panel border border-border-default rounded px-2 py-1 text-text-primary"
          disabled={eligibility.isLoading || eligibleTypes.length === 0}
        >
          <option value="">— select —</option>
          {eligibleTypes.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        {eligibility.data && eligibleTypes.length === 0 && (
          <div className="mt-1 text-xs text-text-muted">
            No alt-bar types eligible for this source.
          </div>
        )}
      </label>

      <label className="block text-sm">
        <div className="text-text-muted mb-1">N (threshold)</div>
        <input
          type="text"
          value={thresholdInput}
          onChange={(e) => setThresholdInput(e.target.value)}
          placeholder="e.g. 1k, 500m, 1.5M"
          className="w-full bg-bg-panel border border-border-default rounded px-2 py-1 font-mono text-text-primary"
        />
        {thresholdInput && !isValidSi(thresholdInput) && (
          <div className="mt-1 text-xs text-accent-red">
            Invalid SI value. Use suffixes k (1e3), M (1e6), G (1e9), m (1e-3), u (1e-6).
          </div>
        )}
      </label>

      <Button type="submit" variant="primary" disabled={!canSubmit}>
        {aggregate.isPending ? "Submitting…" : "Aggregate"}
      </Button>
    </form>
  );
}
