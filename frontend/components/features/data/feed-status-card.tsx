"use client";

// Status panel for an aggregated feed: fidelity grid, JSON viewer, EqI*-source banner,
// and Continue / Delete actions.

import { useEffect, useMemo, useRef } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { json } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { pickProxyBanner } from "@/lib/data/eqi-banner";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import {
  makeFeedJobKey,
  useDataJobsStore,
} from "@/lib/stores/data-jobs-store";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";
import type {
  AggregateRequest,
  FeedDefinition,
  FidelityInfo,
  BuildInfo,
} from "@/types/data-tab";

interface Props {
  exchange: string;
  asset: string;
  feedId: string;
}

export function FeedStatusCard({ exchange, asset, feedId }: Props) {
  const editorContainerRef = useRef<HTMLDivElement>(null);
  const editorViewRef = useRef<EditorView | null>(null);
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const setJob = useDataJobsStore((s) => s.setJob);
  const clearJob = useDataJobsStore((s) => s.clearJob);
  const closePanel = useDataSelectionStore((s) => s.close);

  const status = useQuery({
    queryKey: ["data", "feed-status", exchange, asset, feedId],
    queryFn: ({ signal }) => dataApi.getFeedStatus(exchange, asset, feedId, signal),
  });

  // Canonical source of `warnings[]` for the EqIV banner. Harmless for alt bars (returns
  // empty arrays); always fetch.
  const eligibility = useQuery({
    queryKey: ["data", "aggregation-options", exchange, asset, feedId],
    queryFn: ({ signal }) => dataApi.getAggregationOptions(exchange, asset, feedId, signal),
  });

  const formattedJson = useMemo(() => {
    if (!status.data) return "";
    return JSON.stringify(status.data.definition, null, 2);
  }, [status.data]);

  // Pick the banner copy that matches this feed's reconstruction method. Returns null for
  // tick-source methods (no warning needed) and for non-imbalance feeds.
  const reconstructionMethod =
    status.data?.definition.fidelity?.imbalance_reconstruction_method ?? null;
  const banner = eligibility.data
    ? pickProxyBanner(eligibility.data.warnings, reconstructionMethod)
    : null;

  const definition = status.data?.definition;
  const isAltBar = definition?.kind === "OHLCV_AltBar";

  const continueMutation = useMutation({
    mutationFn: () => {
      if (!definition) throw new Error("Feed definition not loaded yet.");
      const body = buildContinueRequestBody(definition);
      return dataApi.postAggregate(exchange, asset, body);
    },
    onSuccess: (resp) => {
      if (!("job_id" in resp)) {
        toast(`${resp.feed_id}: already up to date`, "info");
        return;
      }
      const key = makeFeedJobKey(exchange, asset, feedId);
      setJob(key, resp.job_id);
      toast(`Continuing ${feedId} (job ${resp.job_id.slice(0, 8)})`, "success");
      queryClient.invalidateQueries({ queryKey: ["data", "exchange-assets", exchange] });
    },
    onError: (err) => {
      if (err instanceof DataApiError && err.code === "feed_already_locked") {
        const body = err.body as { existing_job_id?: string } | null;
        const jobIdHint = body?.existing_job_id ?? "(unknown)";
        toast(`Locked: another job is running on ${feedId} (${jobIdHint.slice(0, 8)})`, "error");
        return;
      }
      if (err instanceof DataApiError && err.code === "resume_unsupported") {
        toast(`${feedId}: legacy feed — Delete and re-aggregate to rebuild.`, "error");
        return;
      }
      toast(err instanceof Error ? err.message : String(err), "error");
    },
  });

  const deleteMutation = useMutation({
    mutationFn: () => dataApi.deleteFeed(exchange, asset, feedId),
    onSuccess: () => {
      toast(`Deleted ${feedId}`, "success");
      // Clear any persisted SSE entry tied to the deleted feed so the in-progress strip
      // doesn't keep trying to reconnect to a stale job stream.
      clearJob(makeFeedJobKey(exchange, asset, feedId));
      // WebApi proxy has a ~2s catalog cache; follow-up invalidate bypasses it.
      const queryKey = ["data", "exchange-assets", exchange];
      queryClient.invalidateQueries({ queryKey });
      setTimeout(() => queryClient.invalidateQueries({ queryKey }), 2500);
      closePanel();
    },
    onError: (err) => {
      if (err instanceof DataApiError && err.code === "feed_already_locked") {
        const body = err.body as { existing_job_id?: string } | null;
        const jobIdHint = body?.existing_job_id ?? "(unknown)";
        toast(`Cancel job ${jobIdHint.slice(0, 8)} before deleting ${feedId}`, "error");
        return;
      }
      toast(err instanceof Error ? err.message : String(err), "error");
    },
  });

  useEffect(() => {
    if (!editorContainerRef.current) return;

    if (editorViewRef.current) {
      editorViewRef.current.dispatch({
        changes: {
          from: 0,
          to: editorViewRef.current.state.doc.length,
          insert: formattedJson,
        },
      });
      return;
    }

    const state = EditorState.create({
      doc: formattedJson,
      extensions: [
        json(),
        oneDark,
        // `editable: () => false` is required in addition to readOnly to suppress the
        // IME ghost cursor that otherwise renders.
        EditorState.readOnly.of(true),
        EditorView.editable.of(false),
        EditorView.theme({
          "&": { fontSize: "12px", maxHeight: "320px" },
          ".cm-scroller": { overflow: "auto" },
        }),
      ],
    });
    editorViewRef.current = new EditorView({
      state,
      parent: editorContainerRef.current,
    });

    return () => {
      editorViewRef.current?.destroy();
      editorViewRef.current = null;
    };
  }, [formattedJson]);

  const handleDelete = () => {
    if (!window.confirm(`Delete feed ${feedId}? This cannot be undone.`)) return;
    deleteMutation.mutate();
  };

  return (
    <div className="space-y-3">
      <div className="text-xs text-text-muted uppercase tracking-wide">Asset</div>
      <div className="font-mono text-sm text-text-primary">{asset}</div>
      <div className="text-xs text-text-muted uppercase tracking-wide">Feed</div>
      <div className="font-mono text-sm text-text-primary">{feedId}</div>

      {banner && (
        <div
          role="alert"
          className="border border-accent-yellow/50 bg-accent-yellow/10 text-accent-yellow px-3 py-2 rounded text-sm"
        >
          {banner}
        </div>
      )}

      {status.isLoading && (
        <div className="text-text-secondary text-sm">Loading status…</div>
      )}
      {status.error && (
        <div className="text-accent-red text-sm">
          {status.error instanceof Error ? status.error.message : String(status.error)}
        </div>
      )}

      {definition && isAltBar && (
        <FidelitySummary fidelity={definition.fidelity} build={definition.build} />
      )}

      {status.data && (
        <div ref={editorContainerRef} className="border border-border-subtle rounded" />
      )}

      {definition && isAltBar && (
        <div className="flex gap-2 pt-2">
          <Button
            type="button"
            variant="primary"
            onClick={() => continueMutation.mutate()}
            disabled={continueMutation.isPending || deleteMutation.isPending}
          >
            {continueMutation.isPending ? "Continuing…" : "Continue"}
          </Button>
          <Button
            type="button"
            variant="danger"
            onClick={handleDelete}
            disabled={deleteMutation.isPending || continueMutation.isPending}
          >
            {deleteMutation.isPending ? "Deleting…" : "Delete"}
          </Button>
        </div>
      )}
    </div>
  );
}

interface FidelitySummaryProps {
  fidelity?: FidelityInfo | null;
  build?: BuildInfo | null;
}

function FidelitySummary({ fidelity, build }: FidelitySummaryProps) {
  if (!fidelity && !build) return null;
  const rows: { label: string; value: string }[] = [];
  if (build?.bar_count != null)
    rows.push({ label: "Bars", value: build.bar_count.toLocaleString() });
  if (fidelity?.actual_overshoot_pct != null)
    rows.push({
      label: "Actual overshoot",
      value: `${fidelity.actual_overshoot_pct.toFixed(2)} %`,
    });
  if (fidelity?.max_overshoot_pct != null)
    rows.push({
      label: "Max overshoot",
      value: `${fidelity.max_overshoot_pct.toFixed(2)} %`,
    });
  if (fidelity?.estimated_overshoot_pct != null)
    rows.push({
      label: "Estimated overshoot",
      value: `${fidelity.estimated_overshoot_pct.toFixed(2)} %`,
    });
  if (fidelity?.n_factor != null)
    rows.push({ label: "N-factor", value: fidelity.n_factor.toFixed(1) });
  if (build?.run_count != null && build.run_count > 1)
    rows.push({ label: "Runs", value: String(build.run_count) });

  if (rows.length === 0) return null;

  return (
    <div className="border border-border-subtle rounded px-3 py-2">
      <div className="text-xs text-text-muted uppercase tracking-wide mb-1">Fidelity</div>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-sm">
        {rows.map((r) => (
          <div key={r.label} className="contents">
            <dt className="text-text-secondary">{r.label}</dt>
            <dd className="font-mono text-text-primary text-right">{r.value}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

function buildContinueRequestBody(def: FeedDefinition): AggregateRequest {
  const sourceFeedId = def.source?.feed;
  const typeCode = def.type?.code;
  const threshold = def.threshold;
  if (!sourceFeedId || !typeCode || !threshold) {
    throw new Error(
      "Cannot continue: existing manifest is missing source / type / threshold metadata.",
    );
  }
  const unit = threshold.unit;
  if (unit !== "base_asset" && unit !== "quote_asset" && unit !== "trades") {
    throw new Error(`Unsupported threshold unit '${unit}' on existing feed.`);
  }
  if (threshold.input_mode === "convenience" && threshold.convenience_input) {
    return {
      source_feed_id: sourceFeedId,
      type_code: typeCode,
      threshold: null,
      threshold_unit: unit,
      input_mode: "convenience",
      convenience_input: threshold.convenience_input,
    };
  }
  return {
    source_feed_id: sourceFeedId,
    type_code: typeCode,
    threshold: threshold.value,
    threshold_unit: unit,
    input_mode: "absolute",
    convenience_input: null,
  };
}
