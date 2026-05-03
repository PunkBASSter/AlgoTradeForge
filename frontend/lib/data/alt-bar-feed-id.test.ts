import { describe, it, expect } from "vitest";
import { parseAltBarFeedId } from "./alt-bar-feed-id";

// Reviewer Issue F1 — parser must mirror C# AltBarFeedId.TryParse. The previous ad-hoc
// `id.split("_")[1]` worked only for the current 3-segment grammar; this parser fails
// closed on any deviation so a future grammar shift surfaces here, not as a silent
// outcome-hint mis-render in the UI.

describe("parseAltBarFeedId", () => {
  it("parses canonical EqV_1m_1000", () => {
    const r = parseAltBarFeedId("EqV_1m_1000");
    expect(r).toEqual({ typeCode: "EqV", sourceCode: "1m", threshold: "1000", isSidecar: false });
  });

  it("parses suffixed thresholds verbatim (no SI expansion)", () => {
    expect(parseAltBarFeedId("EqD_1h_500k")?.threshold).toBe("500k");
    expect(parseAltBarFeedId("EqV_5m_2M")?.threshold).toBe("2M");
  });

  it("flags the .flow sidecar suffix", () => {
    const r = parseAltBarFeedId("EqI_ticks_500.flow");
    expect(r).toMatchObject({ typeCode: "EqI", sourceCode: "ticks", threshold: "500", isSidecar: true });
  });

  it("returns null for unknown type codes (closed set)", () => {
    expect(parseAltBarFeedId("Bogus_1m_1000")).toBeNull();
  });

  it("returns null for unknown source codes", () => {
    expect(parseAltBarFeedId("EqV_99q_1000")).toBeNull();
  });

  it("returns null for grammar with extra segments", () => {
    expect(parseAltBarFeedId("EqV_1m_1000_extra")).toBeNull();
  });

  it("returns null for empty input", () => {
    expect(parseAltBarFeedId("")).toBeNull();
  });
});
