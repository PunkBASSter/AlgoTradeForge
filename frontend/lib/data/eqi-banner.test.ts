import { describe, it, expect } from "vitest";
import { pickEqiBanner, pickProxyBanner } from "./eqi-banner";

// The canonical server-side copies live in:
//   src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AltBarWarnings.cs
//   public const string TimeBarEqIProxy           = "Time-bar EqI uses the taker-buy proxy: …";
//   public const string TimeBarEqIDProxy          = "Time-bar EqID uses the per-minute taker-buy-quote sum: …";
//   public const string TimeBarTibApproximation   = "Time-bar EqIT uses a count proxy derived from `taker_buy_vol / vol × trade_count` — …";
//
// These fixtures mirror those strings. A divergence between FE and BE is a bug — it
// means the BE renamed/reworded the constant without telling us. Detection is by stable
// substring (defined inside `eqi-banner.ts`) so minor copy edits stay backwards
// compatible — but removing the substring fails the test loudly.
const EQI_COPY =
  "Time-bar EqI uses the taker-buy proxy: it underestimates intra-bar churn. " +
  "Rebuild from `ticks` for magnitude-sensitive use.";
const EQID_COPY =
  "Time-bar EqID uses the per-minute taker-buy-quote sum: it underestimates intra-bar " +
  "imbalance churn. Rebuild from `ticks` for magnitude-sensitive use.";
const EQIT_COPY =
  "Time-bar EqIT uses a count proxy derived from `taker_buy_vol / vol × trade_count` — " +
  "it assumes equal-sized trades within each minute. Rebuild from `ticks` for accurate " +
  "participation imbalance.";

describe("pickEqiBanner (legacy entry point — EqI only)", () => {
  it("returns the EqI proxy warning verbatim from the server array", () => {
    const result = pickEqiBanner([EQI_COPY]);
    expect(result).toBe(EQI_COPY);
  });

  it("returns null when no EqI proxy warning is present", () => {
    expect(pickEqiBanner([])).toBeNull();
    expect(pickEqiBanner(["unrelated warning"])).toBeNull();
  });

  it("ignores non-EqI warnings even when they're imbalance-related", () => {
    // The legacy entry point matches only the EqI substring — EqID/EqIT warnings should
    // pass through untouched. Form-page consumers that need the full set should call
    // pickProxyBanner with a method tag.
    expect(pickEqiBanner([EQID_COPY, EQIT_COPY])).toBeNull();
  });

  it("matches by the 'taker-buy proxy' substring (TRD §10.1 detection key)", () => {
    const reworded =
      "Time-bar EqI: the taker-buy proxy reconstruction is approximate.";
    expect(pickEqiBanner([reworded])).toBe(reworded);
  });
});

describe("pickProxyBanner (per-method dispatch)", () => {
  const allWarnings = [EQI_COPY, EQID_COPY, EQIT_COPY];

  it("dispatches m1_taker_buy_proxy → EqI banner", () => {
    expect(pickProxyBanner(allWarnings, "m1_taker_buy_proxy")).toBe(EQI_COPY);
  });

  it("dispatches m1_taker_buy_quote_proxy → EqID banner", () => {
    expect(pickProxyBanner(allWarnings, "m1_taker_buy_quote_proxy")).toBe(EQID_COPY);
  });

  it("dispatches m1_taker_buy_count_proxy → EqIT banner (double-proxy approximation)", () => {
    expect(pickProxyBanner(allWarnings, "m1_taker_buy_count_proxy")).toBe(EQIT_COPY);
  });

  it("returns null for tick-source methods (no proxy warning needed)", () => {
    expect(pickProxyBanner(allWarnings, "tick_signed")).toBeNull();
    expect(pickProxyBanner(allWarnings, "tick_signed_dollar")).toBeNull();
    expect(pickProxyBanner(allWarnings, "tick_signed_count")).toBeNull();
  });

  it("returns null for non-imbalance feeds (method = null)", () => {
    expect(pickProxyBanner(allWarnings, null)).toBeNull();
  });

  it("returns null when the matching banner string is absent from the warnings array", () => {
    // Backend forgot to send the EqID copy even though method tag is set: don't fall
    // back to EqI's banner — discriminate strictly.
    expect(pickProxyBanner([EQI_COPY], "m1_taker_buy_quote_proxy")).toBeNull();
  });
});
