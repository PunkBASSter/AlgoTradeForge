// Friendly label rendering + catalog-feed → DataFeedSubscription mapping. Catalog
// payloads carry structured fields so the FE doesn't re-parse the id for routing —
// only for label rendering.

import type { FeedCatalogEntry } from "@/types/data-tab";
import type {
  AltBarSubscription,
  DataFeedRole,
  DataFeedSubscription,
  SideFeedSubscription,
  TickSubscription,
  TimeBarSubscription,
} from "@/types/api";

/**
 * Formats a number with SI suffixes (k/M/G for ≥1, m/u for <1).
 *
 *   formatSi(1500) -> "1.5k"   formatSi(0.5) -> "500m"
 */
export function formatSi(n: number): string {
  if (!Number.isFinite(n)) return String(n);
  if (n === 0) return "0";

  const abs = Math.abs(n);
  if (abs >= 1e9) return trimZero(n / 1e9) + "G";
  if (abs >= 1e6) return trimZero(n / 1e6) + "M";
  if (abs >= 1e3) return trimZero(n / 1e3) + "k";
  if (abs >= 1) return trimZero(n);
  if (abs >= 1e-3) return trimZero(n * 1e3) + "m";
  if (abs >= 1e-6) return trimZero(n * 1e6) + "u";
  return String(n);
}

function trimZero(n: number): string {
  return n.toFixed(3).replace(/\.?0+$/, "");
}

/**
 * Renders a friendly display label for a feed. Falls back to feed.id when catalog
 * metadata is incomplete — a stale catalog entry should never hide the feed from
 * the picker.
 */
export function formatFeedLabel(feed: FeedCatalogEntry): string {
  switch (feed.kind) {
    case "OHLCV_TimeBar":
      return feed.interval ?? feed.id;

    case "OHLCV_AltBar":
    case "aggregated":
      if (feed.type_code && feed.interval && feed.threshold_value !== null) {
        return `${feed.type_code}/${feed.interval}:${formatSi(feed.threshold_value)}`;
      }
      return feed.id;

    case "Tick":
      return "Ticks";

    case "Side":
      return feed.id;

    default:
      return feed.id;
  }
}

/**
 * Maps a catalog feed entry into a polymorphic DataFeedSubscription, normalizing the
 * legacy storage `kind` (`OHLCV_TimeBar`, etc.) to the polymorphic-discriminator names
 * (`TimeBar` / `AltBar` / `Tick` / `Side`) used on the request wire. Returns null when
 * the requested role is incompatible with the catalog kind.
 */
export function feedToSubscription(
  assetName: string,
  exchange: string,
  feed: FeedCatalogEntry,
  role: DataFeedRole,
): DataFeedSubscription | null {
  switch (feed.kind) {
    case "OHLCV_TimeBar": {
      if (role !== "Primary" || !feed.interval) return null;
      const sub: TimeBarSubscription = {
        kind: "TimeBar",
        role: "Primary",
        assetName,
        exchange,
        timeFrame: feed.interval,
      };
      return sub;
    }

    case "OHLCV_AltBar":
    case "aggregated": {
      if (role !== "Primary") return null;
      const sub: AltBarSubscription = {
        kind: "AltBar",
        role: "Primary",
        assetName,
        exchange,
        feedId: feed.id,
      };
      return sub;
    }

    case "Tick": {
      if (role !== "Primary") return null;
      const sub: TickSubscription = {
        kind: "Tick",
        role: "Primary",
        assetName,
        exchange,
      };
      return sub;
    }

    case "Side": {
      if (role !== "Side") return null;
      const sub: SideFeedSubscription = {
        kind: "Side",
        role: "Side",
        assetName,
        exchange,
        feedId: feed.id,
      };
      return sub;
    }

    default:
      return null;
  }
}

/** Whether a catalog kind can fill the given role slot. */
export function isFeedEligibleForRole(feed: FeedCatalogEntry, role: DataFeedRole): boolean {
  if (role === "Primary") {
    return (
      feed.kind === "OHLCV_TimeBar" ||
      feed.kind === "OHLCV_AltBar" ||
      feed.kind === "aggregated" ||
      feed.kind === "Tick"
    );
  }
  // Side role accepts top-level side feeds AND alt-bar sidecars (id suffixed `.flow`);
  // both arrive flagged as kind="Side".
  return feed.kind === "Side";
}
