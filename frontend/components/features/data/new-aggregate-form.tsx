"use client";

// New-aggregate form. Source / Type / N / Aggregate. The Type dropdown is filtered by
// the eligibility-options endpoint; the EqIV banner copy is pulled byte-identical from
// the same endpoint's `warnings[]`. N input accepts SI suffixes (case-sensitive: k/M/G
// for positive powers, m/u for negative).

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { isValidSi } from "@/lib/data/si-suffix";
import { pickEqiBanner } from "@/lib/data/eqi-banner";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import type { AggregateRequest, FeedCatalogEntry } from "@/types/data-tab";
import { parseAltBarFeedId } from "@/lib/data/alt-bar-feed-id";

function thresholdUnitFor(typeCode: string): AggregateRequest["threshold_unit"] {
  // EqT / EqIT count records (both threshold on trade counts); EqD / EqID are in quote
  // currency (notional dollars); EqV / EqIV / Range / Renko share the base_asset axis on
  // the wire.
  if (typeCode === "EqT" || typeCode === "EqIT") return "trades";
  if (typeCode === "EqD" || typeCode === "EqID") return "quote_asset";
  return "base_asset";
}

interface Props {
  exchange: string;
  asset: string;
  sourceFeed: FeedCatalogEntry;
  /**
   * Extra alt-bar feeds in the same row eligible as re-aggregation sources. The dropdown
   * lists `sourceFeed` plus these; when undefined, runs in single-source mode.
   */
  eligibleSources?: FeedCatalogEntry[];
  /** Called with the upstream-assigned jobId on 202; parent persists for SSE resume. */
  onJobAccepted?: (jobId: string, outcomeFeedIdHint: string) => void;
}

export function NewAggregateForm({ exchange, asset, sourceFeed, eligibleSources, onJobAccepted }: Props) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const [selectedSourceId, setSelectedSourceId] = useState<string>(sourceFeed.id);
  const [typeCode, setTypeCode] = useState<string>("");
  const [thresholdInput, setThresholdInput] = useState("");

  // Always include the primary sourceFeed; dedup against eligibleSources which may
  // include the same id when computed loosely upstream.
  const sourceOptions: FeedCatalogEntry[] = [
    sourceFeed,
    ...(eligibleSources ?? []).filter((f) => f.id !== sourceFeed.id),
  ];

  // Drives both the type dropdown and the EqIV banner. Re-keys on selectedSourceId so
  // picking a different source re-fetches the eligibility set.
  const eligibility = useQuery({
    queryKey: ["data", "aggregation-options", exchange, asset, selectedSourceId],
    queryFn: ({ signal }) =>
      dataApi.getAggregationOptions(exchange, asset, selectedSourceId, signal),
  });

  const banner = eligibility.data ? pickEqiBanner(eligibility.data.warnings) : null;
  const eligibleTypes = eligibility.data?.eligible_types ?? [];

  const aggregate = useMutation({
    mutationFn: (body: AggregateRequest) => dataApi.postAggregate(exchange, asset, body),
    onSuccess: (resp) => {
      // Outcome hint mirrors the server's outcome feed-id grammar: for an alt-bar source
      // the hint embeds the source's SourceCode (e.g. EqV_1m_1000 → 1m); otherwise the
      // source id is used directly. Use the canonical parser so a future grammar change
      // surfaces here rather than silently mis-rendering.
      const selectedSource = sourceOptions.find((s) => s.id === selectedSourceId)!;
      let sourceComponentForHint = selectedSourceId;
      if (selectedSource.kind === "OHLCV_AltBar") {
        const parsed = parseAltBarFeedId(selectedSource.id);
        if (parsed) sourceComponentForHint = parsed.sourceCode;
      }
      const outcomeHint = `${typeCode}_${sourceComponentForHint}_${thresholdInput}`;
      onJobAccepted?.(resp.job_id, outcomeHint);
      toast(`Queued ${outcomeHint} (job ${resp.job_id.slice(0, 8)})`, "success");
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

    // Convenience-input mode: server resolves the threshold via the canonical SI
    // grammar. Passing the original suffix string verbatim preserves it in the
    // manifest's `convenience_input` field.
    aggregate.mutate({
      source_feed_id: selectedSourceId,
      type_code: typeCode,
      threshold: null,
      threshold_unit: thresholdUnitFor(typeCode),
      input_mode: "convenience",
      convenience_input: thresholdInput,
      overwrite_existing: false,
    });
  };

  return (
    <form className="space-y-3" onSubmit={handleSubmit}>
      <div className="text-xs text-text-muted uppercase tracking-wide">Aggregate</div>

      <label className="block text-sm">
        <div className="text-text-muted mb-1">Source</div>
        {sourceOptions.length === 1 ? (
          <div className="font-mono text-text-primary">{sourceFeed.id}</div>
        ) : (
          <select
            value={selectedSourceId}
            onChange={(e) => {
              setSelectedSourceId(e.target.value);
              // Reset type — eligible types differ per source.
              setTypeCode("");
            }}
            className="w-full bg-bg-panel border border-border-default rounded px-2 py-1 font-mono text-text-primary"
          >
            {sourceOptions.map((s) => (
              <option key={s.id} value={s.id}>
                {s.id}
                {s.kind === "OHLCV_AltBar" ? " (re-aggregate)" : ""}
              </option>
            ))}
          </select>
        )}
      </label>

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
