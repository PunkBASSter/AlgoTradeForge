// The EqI banner string is owned by the backend (`AltBarWarnings.TimeBarEqIProxy`) and
// flows verbatim through `warnings[]` on /aggregation-options. The detection substring
// stays stable across copy revisions so FE and BE need not be released in lockstep —
// eqi-banner.test.tsx will catch a substring change.

const EQI_PROXY_KEY = "taker-buy proxy";

/**
 * Returns the EqI time-bar-proxy warning verbatim, or `null` if no such warning is
 * present.
 */
export function pickEqiBanner(warnings: readonly string[]): string | null {
  return warnings.find((w) => w.includes(EQI_PROXY_KEY)) ?? null;
}
