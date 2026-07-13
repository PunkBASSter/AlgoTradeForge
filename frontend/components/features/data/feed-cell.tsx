"use client";

// Single-cell renderer for the asset×feed grid.
//   Materialize  — declared/on-demand feed not yet on disk; click triggers postMaterialize.
//   `+` framed   — present aggregation source; click opens status view.
//   `+` bare     — present feed; click opens status view.
//   dot          — sidecar feed indicator.

import type { CSSProperties } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { useToast } from "@/components/ui/toast";
import { exchangeSymbolOf } from "@/lib/data/coverage-mapping";
import type { AssetCatalogEntry, FeedCatalogEntry, FeedKind } from "@/types/data-tab";

interface Props {
  asset: AssetCatalogEntry;
  feedColumn: FeedCatalogEntry;
  style: CSSProperties;
  onView?: (asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
}

// Raw sources (time bars + ticks) surface the framed `+` affordance to signal aggregability.
const AGGREGATION_SOURCE_KINDS: ReadonlySet<FeedKind> = new Set([
  "OHLCV_TimeBar",
  "Tick",
]);

// Isolated so hooks only mount when the cell is actually absent — avoids requiring
// ToastProvider / QueryClientProvider in grid tests that render only present-feed cells.
interface MaterializeCellProps {
  asset: AssetCatalogEntry;
  feedColumn: FeedCatalogEntry;
  style: CSSProperties;
}

function MaterializeCell({ asset, feedColumn, style }: MaterializeCellProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const materialize = useMutation({
    mutationFn: () =>
      dataApi.postMaterialize({
        exchange: asset.exchange,
        symbol: asset.symbol,
        feed: feedColumn.id,
      }),
    onSuccess: (resp) => {
      toast(
        `Materializing ${feedColumn.id} (job ${resp.job_id.slice(0, 8)}) — see Jobs panel`,
        "success",
      );
      queryClient.invalidateQueries({ queryKey: ["data", "jobs"] });
      queryClient.invalidateQueries({
        queryKey: ["data", "coverage", asset.exchange, exchangeSymbolOf(asset), asset.type],
      });
    },
    onError: (err) => {
      if (err instanceof DataApiError && err.code === "feed_busy") {
        toast("Already materializing — see Jobs panel", "info");
        return;
      }
      toast(err instanceof Error ? err.message : String(err), "error");
    },
  });

  return (
    <button
      type="button"
      onClick={() => materialize.mutate()}
      disabled={materialize.isPending}
      style={style}
      className="absolute flex items-center justify-center text-accent-blue hover:bg-bg-hover disabled:opacity-50 transition-colors text-xs font-medium"
      aria-label={`Materialize ${feedColumn.id} for ${asset.display_name}`}
    >
      {materialize.isPending ? "…" : "Materialize"}
    </button>
  );
}

export function FeedCell({ asset, feedColumn, style, onView }: Props) {
  const present = asset.feeds.find((f) => f.id === feedColumn.id);
  const isAggregationSource = AGGREGATION_SOURCE_KINDS.has(feedColumn.kind);

  if (!present) {
    return <MaterializeCell asset={asset} feedColumn={feedColumn} style={style} />;
  }

  const hasSidecar = present.sidecar !== null;

  if (isAggregationSource) {
    return (
      <button
        type="button"
        onClick={() => onView?.(asset, present)}
        style={style}
        className="absolute flex items-center justify-center gap-1 text-text-secondary transition-colors text-sm group"
        aria-label={`View ${feedColumn.id} on ${asset.display_name}`}
      >
        <span
          className="inline-flex items-center justify-center w-5 h-5 rounded border border-border-subtle group-hover:border-accent-blue group-hover:bg-bg-hover group-hover:text-accent-blue transition-colors"
        >
          +
        </span>
        {hasSidecar && (
          <span
            aria-label="has sidecar"
            title="Sidecar (.flow) feed available"
            className="inline-block w-1.5 h-1.5 rounded-full bg-accent-blue"
          />
        )}
      </button>
    );
  }

  return (
    <button
      type="button"
      onClick={() => onView?.(asset, present)}
      style={style}
      className="absolute flex items-center justify-center gap-1 text-text-secondary hover:bg-bg-hover hover:text-text-primary transition-colors text-sm"
      aria-label={`View ${feedColumn.id} on ${asset.display_name}`}
    >
      <span>+</span>
      {hasSidecar && (
        <span
          aria-label="has sidecar"
          title="Sidecar (.flow) feed available"
          className="inline-block w-1.5 h-1.5 rounded-full bg-accent-blue"
        />
      )}
    </button>
  );
}
