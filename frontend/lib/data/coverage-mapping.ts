// Maps a FeedCatalogEntry to its archive-coverage lookup key.
//
// OHLCV_TimeBar  → { feedName: "candles", interval: feed.id }
// Side           → { feedName: feed.id, interval: "" }
//   interval: "" is a sentinel; the consumer matches coverage entries by
//   `entry.feed_name === feed.id || `${entry.feed_name}_${entry.interval}` === feed.id`
// OHLCV_AltBar / Tick / aggregated → null (no month-coverage semantics)

import type { FeedCatalogEntry } from "@/types/data-tab";

// The API's coverage/load `symbol` is the EXCHANGE symbol. The catalog `symbol` is the on-disk
// dir per AssetPathConvention ({SYMBOL}_perp for perpetual/future); display_name is a UI label
// ("BTCUSDT-perp") and must never be used as an API key.
export function exchangeSymbolOf(asset: { symbol: string }): string {
  return asset.symbol.endsWith("_perp") ? asset.symbol.slice(0, -"_perp".length) : asset.symbol;
}

export function mapCatalogFeedToCoverage(
  feed: FeedCatalogEntry,
): { feedName: string; interval: string } | null {
  switch (feed.kind) {
    case "OHLCV_TimeBar":
      return { feedName: "candles", interval: feed.id };
    case "Side":
      return { feedName: feed.id, interval: "" };
    case "Tick":
      return { feedName: "ticks", interval: "" };
    default:
      return null;
  }
}
