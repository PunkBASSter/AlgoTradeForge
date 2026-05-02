// Phase 3 — surfaces the EqI yellow-banner copy from the eligibility-options API
// (P3-19). The string is owned by the backend (`AltBarWarnings.TimeBarEqIProxy`) and
// flows verbatim through the warnings[] array on /aggregation-options. The FE's only
// job is to detect that it's the EqI-proxy warning and render it byte-identical.
//
// Detection key: "taker-buy proxy" substring (TRD §10.1, P2b-13). If the canonical copy
// changes server-side, the substring stays stable so the FE doesn't need a coordinated
// update. If the substring ever changes, the test in eqi-banner.test.tsx will catch it.

const EQI_PROXY_KEY = "taker-buy proxy";

/**
 * Returns the EqI time-bar-proxy warning string from a server-supplied `warnings[]`
 * array, or `null` if no such warning is present (i.e. this feed is NOT a time-bar-EqI).
 *
 * Rendered byte-identical to the API value — no client-side string composition.
 */
export function pickEqiBanner(warnings: readonly string[]): string | null {
  return warnings.find((w) => w.includes(EQI_PROXY_KEY)) ?? null;
}
