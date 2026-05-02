"use client";

// Single-cell renderer for the asset×feed grid (P3-14).
//   `+`  — feed-column is absent on this asset; click opens the new-aggregate form.
//   `−`  — feed is present AND deletable (OHLCV_AltBar only). Click opens delete confirm.
//   ·    — sidecar-bearing aggregated cell renders an indicator dot.
//   (blank) — feed present but not deletable (time bars, ticks, side feeds).
//
// Sidebar interactions are wired in S10 via the Zustand store; this stage's component
// is pure-presentational so the asset-feed-grid test can assert DOM count cleanly.

import type { CSSProperties } from "react";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

interface Props {
  asset: AssetCatalogEntry;
  feedColumn: FeedCatalogEntry;
  style: CSSProperties;
  onAdd?: (asset: AssetCatalogEntry, sourceFeed: FeedCatalogEntry) => void;
  onView?: (asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
}

export function FeedCell({ asset, feedColumn, style, onAdd, onView }: Props) {
  const present = asset.feeds.find((f) => f.id === feedColumn.id);

  // Absent: render `+` affordance for alt-bar-eligible source feeds. Time-bar columns
  // are always present per asset (the "all assets share the schema" invariant), so the
  // empty case is uncommon for time bars; we still render `+` defensively for any
  // missing column.
  if (!present) {
    return (
      <button
        type="button"
        onClick={() => onAdd?.(asset, feedColumn)}
        style={style}
        className="absolute flex items-center justify-center text-text-muted hover:bg-bg-hover hover:text-accent-blue transition-colors text-sm"
        aria-label={`Aggregate ${feedColumn.id} for ${asset.asset}`}
      >
        +
      </button>
    );
  }

  const isDeletable = feedColumn.kind === "OHLCV_AltBar" || feedColumn.kind === "aggregated";
  const hasSidecar = present.sidecar !== null;

  return (
    <button
      type="button"
      onClick={() => onView?.(asset, present)}
      style={style}
      className="absolute flex items-center justify-center gap-1 text-text-secondary hover:bg-bg-hover hover:text-text-primary transition-colors text-sm"
      aria-label={`View ${feedColumn.id} on ${asset.asset}`}
    >
      <span>{isDeletable ? "−" : ""}</span>
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
