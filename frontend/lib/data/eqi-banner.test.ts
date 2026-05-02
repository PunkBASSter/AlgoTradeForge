import { describe, it, expect } from "vitest";
import { pickEqiBanner } from "./eqi-banner";

// The canonical server-side copy lives in:
//   src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AltBarWarnings.cs
//   public const string TimeBarEqIProxy =
//     "Time-bar EqI uses the taker-buy proxy: it underestimates intra-bar churn. " +
//     "Rebuild from `ticks` for magnitude-sensitive use.";
//
// This fixture mirrors that string. A divergence between the two is a bug — it means
// the BE renamed the constant without telling us. The detection-key (substring "taker-buy
// proxy") is what makes the test resilient: if the BE rewords slightly, this test still
// passes as long as the substring stays. If the substring is removed, the test fails
// loudly so we can re-coordinate.
const CANONICAL_COPY =
  "Time-bar EqI uses the taker-buy proxy: it underestimates intra-bar churn. " +
  "Rebuild from `ticks` for magnitude-sensitive use.";

describe("pickEqiBanner", () => {
  it("returns the EqI proxy warning verbatim from the server array", () => {
    const result = pickEqiBanner([CANONICAL_COPY]);
    expect(result).toBe(CANONICAL_COPY);   // byte-identical
  });

  it("returns null when no EqI proxy warning is present", () => {
    expect(pickEqiBanner([])).toBeNull();
    expect(pickEqiBanner(["unrelated warning"])).toBeNull();
  });

  it("ignores non-EqI warnings in the same array", () => {
    const warnings = [
      "Some unrelated server warning",
      CANONICAL_COPY,
      "Another unrelated thing",
    ];
    expect(pickEqiBanner(warnings)).toBe(CANONICAL_COPY);
  });

  it("matches by the 'taker-buy proxy' substring (TRD §10.1 detection key)", () => {
    const reworded =
      "Time-bar EqI: the taker-buy proxy reconstruction is approximate.";
    expect(pickEqiBanner([reworded])).toBe(reworded);   // still byte-identical to the server's reworded copy
  });
});
