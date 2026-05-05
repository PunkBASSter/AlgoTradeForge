// Column ordering for the asset×feed grid:
//   1. Time bars (by interval ascending)
//   2. Aggregated alt bars (by type_code then threshold_value)
//   3. Ticks
//   4. Side feeds

import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

// Lower bucket = leftmost. Distinct from `kind` so per-bucket tie-breakers branch
// without re-deriving the kind every comparison.
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
 * Interval string → seconds for chronological sorting. Lex sort orders "1d" < "1h" <
 * "1m" — the opposite of what users expect. Unparseable inputs return MAX_SAFE_INTEGER
 * so they fall to the end of the bucket rather than colliding at 0.
 */
function intervalSeconds(s: string | null | undefined): number {
  if (!s) return Number.MAX_SAFE_INTEGER;
  const match = /^(\d+)([smhd])$/.exec(s);
  if (!match) return Number.MAX_SAFE_INTEGER;
  const n = Number(match[1]);
  switch (match[2]) {
    case "s": return n;
    case "m": return n * 60;
    case "h": return n * 3600;
    case "d": return n * 86_400;
    default:  return Number.MAX_SAFE_INTEGER;
  }
}

/**
 * Column-order comparator. Within a bucket: time bars by duration; alt bars by
 * type_code then threshold_value; side feeds by id.
 */
export function compareFeed(a: FeedCatalogEntry, b: FeedCatalogEntry): number {
  const ba = bucket(a);
  const bb = bucket(b);
  if (ba !== bb) return ba - bb;

  if (ba === 1) {
    const dur = intervalSeconds(a.interval) - intervalSeconds(b.interval);
    if (dur !== 0) return dur;
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
 * Ordered union of feed entries across visible assets — one column per unique feed
 * `id`. First occurrence's metadata wins (assets in the same exchange share schema).
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
