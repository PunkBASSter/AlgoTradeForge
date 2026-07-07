// Maps a FeedCatalogEntry to its archive-coverage lookup key.
//
// OHLCV_TimeBar  → { feedName: "candles", interval: feed.id }
// Side           → { feedName: feed.id, interval: "" }
//   interval: "" is a sentinel; the consumer matches coverage entries by
//   `entry.feed_name === feed.id || `${entry.feed_name}_${entry.interval}` === feed.id`
// OHLCV_AltBar / Tick / aggregated → null (no month-coverage semantics)

import type { FeedCatalogEntry } from "@/types/data-tab";

export function mapCatalogFeedToCoverage(
  feed: FeedCatalogEntry,
): { feedName: string; interval: string } | null {
  switch (feed.kind) {
    case "OHLCV_TimeBar":
      return { feedName: "candles", interval: feed.id };
    case "Side":
      return { feedName: feed.id, interval: "" };
    default:
      return null;
  }
}
