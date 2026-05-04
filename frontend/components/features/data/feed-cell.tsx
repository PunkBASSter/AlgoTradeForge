"use client";

// Single-cell renderer for the asset×feed grid.
//   `+`  — feed is present on this asset. Two visual variants:
//          • Aggregation-eligible source (TimeBar / AltBar / aggregated / Tick): wrapped in
//            a bordered frame and hovers to an accent-blue outline. Click opens the
//            new-aggregate sidebar with this feed pre-selected as the source.
//          • Other present feeds (Side, sidecar): bare `+`. Click opens the feed-status
//            sidebar.
//   `−`  — feed is absent. Click opens the new-aggregate form for the column (used when
//          a target alt-bar column is missing for this asset).
//   ·    — sidecar-bearing aggregated cell renders an additional indicator dot.
//
// The +/− glyphs are inverted from the underlying button affordance (+ doesn't mean "add",
// it means "the data is present"). The frame on the aggregation-eligible variant is the
// affordance hint that the cell is *interactive for aggregation* — without it, users can't
// tell which `+` cells lead to the create form vs the read-only status view.

import type { CSSProperties } from "react";
import type { AssetCatalogEntry, FeedCatalogEntry, FeedKind } from "@/types/data-tab";

interface Props {
  asset: AssetCatalogEntry;
  feedColumn: FeedCatalogEntry;
  style: CSSProperties;
  onAdd?: (asset: AssetCatalogEntry, sourceFeed: FeedCatalogEntry) => void;
  onView?: (asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
}

// Feed kinds that can act as a source for new alt-bar aggregation. Side feeds and
// (sidecar-only) aggregated entries are excluded — they're informational, not source data.
const AGGREGATION_SOURCE_KINDS: ReadonlySet<FeedKind> = new Set([
  "OHLCV_TimeBar",
  "OHLCV_AltBar",
  "aggregated",
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

  // Aggregation-eligible sources get a framed `+` so users can distinguish "click to start
  // an aggregation from this source" from "click to view this informational feed".
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
