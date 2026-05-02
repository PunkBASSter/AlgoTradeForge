// Phase 4 (P4-17) — friendly label rendering + catalog-feed → DataFeedSubscription mapping.
// The TRD §3.3 alt-bar grammar lives on the C# side (AltBarFeedId.TryParse); this is a
// lightweight FE projection for display + selection. Catalog payloads carry structured
// fields (kind, type_code, threshold_value, interval, sidecar) so the FE doesn't re-parse
// the id string for routing — only for label rendering.

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
 * Formats a number with TRD §3.4 SI suffixes (k/M/G for ≥1, m/u for <1).
 * Matches what users see in the new-aggregate form (P3-16).
 *
 *   formatSi(1000)     -> "1k"
 *   formatSi(1500)     -> "1.5k"
 *   formatSi(500)      -> "500"
 *   formatSi(0.5)      -> "500m"
 *   formatSi(2_000_000) -> "2M"
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
  // 1.0 → "1", 1.5 → "1.5", 1000 → "1000". Keeps up to 3 significant figures past the decimal.
  return n.toFixed(3).replace(/\.?0+$/, "");
}

/**
 * Renders a friendly display label for a feed (TRD §10.2):
 *   TimeBar  -> "1h"
 *   AltBar   -> "EqV/1m:1k"   (parsed from feed.id per §3.3 grammar)
 *   Tick     -> "Ticks"
 *   Side     -> the feed id verbatim ("funding-rate", "EqI_..._signed.flow")
 *
 * Falls back to feed.id when the catalog metadata is incomplete — reading a stale
 * catalog entry should never hide the feed from the picker.
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
      // Fallback for partial metadata — try splitting the id positionally.
      // Format per §3.3: <TypeCode>_<SourceCode>_<Threshold>[, .flow suffix].
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
 * Maps a catalog feed entry into a polymorphic {@link DataFeedSubscription}. Kind is
 * inferred from the catalog kind; the requested {@link DataFeedRole} is honored when
 * compatible, otherwise the call returns null (e.g. asking for a Side feed as Primary).
 *
 * The wire `kind` from the catalog uses the legacy storage names (`OHLCV_TimeBar`,
 * `OHLCV_AltBar`, `Tick`, `Side`) — this normalizes to the polymorphic-discriminator
 * names (`TimeBar` / `AltBar` / `Tick` / `Side`) emitted on the request wire.
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

/** Eligibility for a given role: which catalog kinds can fill this slot. */
export function isFeedEligibleForRole(feed: FeedCatalogEntry, role: DataFeedRole): boolean {
  if (role === "Primary") {
    return (
      feed.kind === "OHLCV_TimeBar" ||
      feed.kind === "OHLCV_AltBar" ||
      feed.kind === "aggregated" ||
      feed.kind === "Tick"
    );
  }
  // Side role accepts top-level side feeds AND alt-bar sidecars (id suffixed `.flow`).
  // The catalog flags both as kind="Side" via Phase 3 normalization.
  return feed.kind === "Side";
}
