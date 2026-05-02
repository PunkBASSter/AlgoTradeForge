// Phase 3 — column ordering for the asset×feed grid (P3-12). The TRD §3.3 grammar
// parser lives on the C# side; on the wire we get structured fields (kind, type_code,
// threshold_value) on each FeedCatalogEntry, so the FE just sorts on those.
//
// Display rule (TRD §10.1):
//   1. Time bars (canonical, by interval ascending)
//   2. Aggregated alt bars (grouped by type_code, then threshold_value ascending)
//   3. Ticks
//   4. Side feeds (rightmost, dimmed in the cell renderer)

import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

// Bucket lower = leftmost. Distinct from the kind itself so we can branch tie-breakers
// per bucket without re-deriving the kind every comparison.
function bucket(f: FeedCatalogEntry): number {
  switch (f.kind) {
    case "OHLCV_TimeBar":
      return 1;
    case "OHLCV_AltBar":
    case "aggregated":
      return 2;
    case "Tick":
      return 3;
    case "Side":
      return 4;
    default:
      return 5;
  }
}

/**
 * Stable-ish comparator for the column order. Within a bucket:
 *  - Time bars: by `interval` lexically (lowercase: "1m" < "5m" < "1h", which works
 *    because all intervals share a single suffix character — m, h, d).
 *  - Alt bars: by `type_code` then `threshold_value` ascending (TRD §3.3).
 *  - Side feeds: by `id` lexically.
 *
 * NOTE: lexical interval sort is good enough for the canonical set
 * ("1m","3m","5m","15m","30m","1h","4h","1d") because these all begin with a digit;
 * "10m" would sort before "1m" but isn't in our supported set. Add a numeric tier
 * here if/when sub-minute or 10m+ intervals land.
 */
export function compareFeed(a: FeedCatalogEntry, b: FeedCatalogEntry): number {
  const ba = bucket(a);
  const bb = bucket(b);
  if (ba !== bb) return ba - bb;

  if (ba === 1) {
    return (a.interval ?? a.id).localeCompare(b.interval ?? b.id);
  }
  if (ba === 2) {
    const t = (a.type_code ?? "").localeCompare(b.type_code ?? "");
    if (t !== 0) return t;
    return (a.threshold_value ?? 0) - (b.threshold_value ?? 0);
  }
  return a.id.localeCompare(b.id);
}

/**
 * Returns the ordered union of feed entries across visible assets — one column per
 * unique feed `id`. The first occurrence's metadata wins (all assets in the same
 * exchange share the same feed schema, so this is safe).
 *
 * The grid renders these as columns; each cell looks up the asset's local feed by id
 * to decide whether to render `+` (absent) or `−` (present, and an alt-bar so deletable).
 */
export function unionFeedColumns(assets: AssetCatalogEntry[]): FeedCatalogEntry[] {
  const seen = new Map<string, FeedCatalogEntry>();
  for (const asset of assets) {
    for (const feed of asset.feeds) {
      if (!seen.has(feed.id)) seen.set(feed.id, feed);
    }
  }
  return [...seen.values()].sort(compareFeed);
}
