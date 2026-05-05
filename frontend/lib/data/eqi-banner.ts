// Banner copy for time-bar imbalance proxies. Strings are owned by the backend
// (`AltBarWarnings.{TimeBarEqIProxy,TimeBarEqIDProxy,TimeBarTibApproximation}`) and flow
// verbatim through `warnings[]` on /aggregation-options. Detection is by stable substring
// so FE and BE need not be released in lockstep — eqi-banner.test.ts pins each substring.

import type { FidelityInfo } from "@/types/data-tab";

// Substring keys, one per backend-owned banner copy. Each is unique enough to discriminate
// without false positives, but we don't depend on the full sentence so copy edits don't
// break detection.
const PROXY_BANNER_KEYS = {
  TakerBuyVolume: "taker-buy proxy",                  // EqI on time-bar
  TakerBuyQuoteVolume: "per-minute taker-buy-quote",  // EqID on time-bar
  TakerBuyTradeCount: "count proxy derived from",     // EqIT on time-bar
} as const;

type ProxyKind = keyof typeof PROXY_BANNER_KEYS;

/** Maps an `imbalance_reconstruction_method` value to its banner kind, or null. */
function methodToKind(
  method: FidelityInfo["imbalance_reconstruction_method"],
): ProxyKind | null {
  switch (method) {
    case "m1_taker_buy_proxy":
      return "TakerBuyVolume";
    case "m1_taker_buy_quote_proxy":
      return "TakerBuyQuoteVolume";
    case "m1_taker_buy_count_proxy":
      return "TakerBuyTradeCount";
    default:
      return null;
  }
}

/**
 * Returns the EqI time-bar-proxy warning verbatim, or `null` if no such warning is
 * present. Kept as a thin wrapper over `pickProxyBanner` so the existing call sites
 * (form preview, before any feed exists) keep working.
 */
export function pickEqiBanner(warnings: readonly string[]): string | null {
  return findByKey(warnings, PROXY_BANNER_KEYS.TakerBuyVolume);
}

/**
 * Picks the banner copy matching the feed's `imbalance_reconstruction_method`. Returns
 * `null` for tick-source methods (no warning needed) or for non-imbalance feeds.
 */
export function pickProxyBanner(
  warnings: readonly string[],
  method: FidelityInfo["imbalance_reconstruction_method"],
): string | null {
  const kind = methodToKind(method);
  if (kind === null) return null;
  return findByKey(warnings, PROXY_BANNER_KEYS[kind]);
}

function findByKey(warnings: readonly string[], key: string): string | null {
  return warnings.find((w) => w.includes(key)) ?? null;
}
