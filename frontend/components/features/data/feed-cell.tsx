"use client";

// Single-cell renderer for the asset×feed grid.
//   `+` framed   — aggregation-eligible source (click opens create form).
//   `+` bare     — present feed; click opens status view.
//   `−`          — feed absent; click opens create form for that column.
//   dot          — sidecar feed indicator.

import type { CSSProperties } from "react";
import type { AssetCatalogEntry, FeedCatalogEntry, FeedKind } from "@/types/data-tab";

interface Props {
  asset: AssetCatalogEntry;
  feedColumn: FeedCatalogEntry;
  style: CSSProperties;
  onAdd?: (asset: AssetCatalogEntry, sourceFeed: FeedCatalogEntry) => void;
  onView?: (asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
}

// Raw sources open the create form; alt-bars fall through to the status view (which hosts
// Continue + Delete).
const AGGREGATION_SOURCE_KINDS: ReadonlySet<FeedKind> = new Set([
  "OHLCV_TimeBar",
  "Tick",
]);

export function FeedCell({ asset, feedColumn, style, onAdd, onView }: Props) {
  const present = asset.feeds.find((f) => f.id === feedColumn.id);
  const isAggregationSource = AGGREGATION_SOURCE_KINDS.has(feedColumn.kind);

  if (!present) {
    return (
      <button
        type="button"
        onClick={() => onAdd?.(asset, feedColumn)}
        style={style}
        className="absolute flex items-center justify-center text-text-muted hover:bg-bg-hover hover:text-accent-blue transition-colors text-sm"
        aria-label={`No ${feedColumn.id} for ${asset.display_name} — click to create`}
      >
        −
      </button>
    );
  }

  const hasSidecar = present.sidecar !== null;

  if (isAggregationSource) {
    return (
      <button
        type="button"
        onClick={() => onAdd?.(asset, present)}
        style={style}
        className="absolute flex items-center justify-center gap-1 text-text-secondary transition-colors text-sm group"
        aria-label={`Aggregate from ${feedColumn.id} on ${asset.display_name}`}
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
