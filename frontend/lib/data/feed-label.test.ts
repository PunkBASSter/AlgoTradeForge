import { describe, it, expect } from "vitest";
import {
  formatSi,
  formatFeedLabel,
  feedToSubscription,
  isFeedEligibleForRole,
} from "./feed-label";
import type { FeedCatalogEntry } from "@/types/data-tab";

// Helper — construct a FeedCatalogEntry with the minimum required fields and the rest
// nulled (matches the catalog wire shape, where alt-bar metadata is null for non-alt feeds).
function feed(overrides: Partial<FeedCatalogEntry>): FeedCatalogEntry {
  return {
    id: overrides.id ?? "fixture",
    kind: overrides.kind ?? "OHLCV_TimeBar",
    interval: overrides.interval ?? null,
    type_code: overrides.type_code ?? null,
    threshold_value: overrides.threshold_value ?? null,
    sidecar: overrides.sidecar ?? null,
  };
}

describe("formatSi", () => {
  it("returns plain string for integers in [1, 1000)", () => {
    expect(formatSi(1)).toBe("1");
    expect(formatSi(500)).toBe("500");
    expect(formatSi(999)).toBe("999");
  });

  it("uses k suffix for [1e3, 1e6)", () => {
    expect(formatSi(1000)).toBe("1k");
    expect(formatSi(1500)).toBe("1.5k");
    expect(formatSi(999999)).toBe("999.999k");
  });

  it("uses M suffix for [1e6, 1e9)", () => {
    expect(formatSi(1_000_000)).toBe("1M");
    expect(formatSi(2_500_000)).toBe("2.5M");
  });

  it("uses G suffix for ≥1e9", () => {
    expect(formatSi(1_000_000_000)).toBe("1G");
    expect(formatSi(7_500_000_000)).toBe("7.5G");
  });

  it("uses m suffix for [1e-3, 1)", () => {
    expect(formatSi(0.5)).toBe("500m");
    expect(formatSi(0.001)).toBe("1m");
  });

  it("uses u suffix for [1e-6, 1e-3)", () => {
    expect(formatSi(0.000001)).toBe("1u");
    expect(formatSi(0.0005)).toBe("500u");
  });

  it("returns '0' for zero exactly (no SI suffix)", () => {
    expect(formatSi(0)).toBe("0");
  });

  it("falls back to String(n) for non-finite inputs", () => {
    expect(formatSi(Number.NaN)).toBe("NaN");
    expect(formatSi(Number.POSITIVE_INFINITY)).toBe("Infinity");
  });

  it("trims trailing zeros after the decimal point", () => {
    // 1000 → 1.000 → trimmed to "1"
    expect(formatSi(1000)).toBe("1k");
    // 1100 → 1.100 → trimmed to "1.1"
    expect(formatSi(1100)).toBe("1.1k");
  });

  it("handles negative magnitudes via Math.abs comparison", () => {
    // Sign is preserved by trimZero(n / scale).toFixed.
    expect(formatSi(-1000)).toBe("-1k");
    expect(formatSi(-2_500_000)).toBe("-2.5M");
  });
});

describe("formatFeedLabel", () => {
  it("renders TimeBar feeds as their interval", () => {
    expect(formatFeedLabel(feed({ kind: "OHLCV_TimeBar", id: "1m", interval: "1m" }))).toBe("1m");
    expect(formatFeedLabel(feed({ kind: "OHLCV_TimeBar", id: "1h", interval: "1h" }))).toBe("1h");
  });

  it("falls back to id when TimeBar interval is missing", () => {
    expect(formatFeedLabel(feed({ kind: "OHLCV_TimeBar", id: "1m", interval: null }))).toBe("1m");
  });

  it("renders AltBar feeds as TYPE/INTERVAL:THRESHOLD using SI", () => {
    expect(
      formatFeedLabel(
        feed({
          kind: "OHLCV_AltBar",
          id: "EqV_1m_1000",
          type_code: "EqV",
          interval: "1m",
          threshold_value: 1000,
        }),
      ),
    ).toBe("EqV/1m:1k");

    expect(
      formatFeedLabel(
        feed({
          kind: "OHLCV_AltBar",
          id: "EqD_1h_2500000",
          type_code: "EqD",
          interval: "1h",
          threshold_value: 2_500_000,
        }),
      ),
    ).toBe("EqD/1h:2.5M");
  });

  it("renders aggregated kind the same as OHLCV_AltBar", () => {
    expect(
      formatFeedLabel(
        feed({
          kind: "aggregated",
          id: "EqT_1m_500",
          type_code: "EqT",
          interval: "1m",
          threshold_value: 500,
        }),
      ),
    ).toBe("EqT/1m:500");
  });

  it("falls back to id when AltBar metadata is incomplete", () => {
    expect(formatFeedLabel(feed({ kind: "OHLCV_AltBar", id: "EqV_1m_1000" }))).toBe("EqV_1m_1000");
    expect(formatFeedLabel(feed({ kind: "OHLCV_AltBar", id: "EqV_1m_1000", type_code: "EqV" }))).toBe(
      "EqV_1m_1000",
    );
  });

  it("renders Tick feeds as 'Ticks' (kind name, not the id)", () => {
    expect(formatFeedLabel(feed({ kind: "Tick", id: "ticks" }))).toBe("Ticks");
  });

  it("renders Side feeds verbatim by id", () => {
    expect(formatFeedLabel(feed({ kind: "Side", id: "funding-rate" }))).toBe("funding-rate");
    expect(formatFeedLabel(feed({ kind: "Side", id: "EqIV_1m_500_signed.flow" }))).toBe(
      "EqIV_1m_500_signed.flow",
    );
  });
});

describe("feedToSubscription", () => {
  const ASSET = "BTCUSDT_perp";
  const EXCHANGE = "binance";

  it("maps OHLCV_TimeBar+Primary to a TimeBar subscription", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "OHLCV_TimeBar", id: "1h", interval: "1h" }),
      "Primary",
    );
    expect(sub).toEqual({
      kind: "TimeBar",
      role: "Primary",
      assetName: ASSET,
      exchange: EXCHANGE,
      timeFrame: "1h",
    });
  });

  it("returns null for OHLCV_TimeBar with missing interval", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "OHLCV_TimeBar", id: "1h", interval: null }),
      "Primary",
    );
    expect(sub).toBeNull();
  });

  it("returns null for OHLCV_TimeBar requested as Side", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "OHLCV_TimeBar", id: "1h", interval: "1h" }),
      "Side",
    );
    expect(sub).toBeNull();
  });

  it("maps OHLCV_AltBar+Primary to an AltBar subscription with feedId", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "OHLCV_AltBar", id: "EqV_1m_1000", type_code: "EqV", interval: "1m", threshold_value: 1000 }),
      "Primary",
    );
    expect(sub).toEqual({
      kind: "AltBar",
      role: "Primary",
      assetName: ASSET,
      exchange: EXCHANGE,
      feedId: "EqV_1m_1000",
    });
  });

  it("treats aggregated kind the same as OHLCV_AltBar", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "aggregated", id: "EqT_1m_500" }),
      "Primary",
    );
    expect(sub?.kind).toBe("AltBar");
  });

  it("returns null for AltBar requested as Side", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "OHLCV_AltBar", id: "EqV_1m_1000" }),
      "Side",
    );
    expect(sub).toBeNull();
  });

  it("maps Tick+Primary to a Tick subscription", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "Tick", id: "ticks" }),
      "Primary",
    );
    expect(sub).toEqual({
      kind: "Tick",
      role: "Primary",
      assetName: ASSET,
      exchange: EXCHANGE,
    });
  });

  it("returns null for Tick requested as Side", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "Tick", id: "ticks" }),
      "Side",
    );
    expect(sub).toBeNull();
  });

  it("maps Side+Side to a SideFeedSubscription", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "Side", id: "funding-rate" }),
      "Side",
    );
    expect(sub).toEqual({
      kind: "Side",
      role: "Side",
      assetName: ASSET,
      exchange: EXCHANGE,
      feedId: "funding-rate",
    });
  });

  it("returns null for Side requested as Primary", () => {
    const sub = feedToSubscription(
      ASSET,
      EXCHANGE,
      feed({ kind: "Side", id: "funding-rate" }),
      "Primary",
    );
    expect(sub).toBeNull();
  });
});

describe("isFeedEligibleForRole", () => {
  it("Primary slot accepts TimeBar / AltBar / aggregated / Tick", () => {
    expect(isFeedEligibleForRole(feed({ kind: "OHLCV_TimeBar" }), "Primary")).toBe(true);
    expect(isFeedEligibleForRole(feed({ kind: "OHLCV_AltBar" }), "Primary")).toBe(true);
    expect(isFeedEligibleForRole(feed({ kind: "aggregated" }), "Primary")).toBe(true);
    expect(isFeedEligibleForRole(feed({ kind: "Tick" }), "Primary")).toBe(true);
  });

  it("Primary slot rejects Side feeds", () => {
    expect(isFeedEligibleForRole(feed({ kind: "Side" }), "Primary")).toBe(false);
  });

  it("Side slot accepts only Side feeds", () => {
    expect(isFeedEligibleForRole(feed({ kind: "Side" }), "Side")).toBe(true);
    expect(isFeedEligibleForRole(feed({ kind: "OHLCV_TimeBar" }), "Side")).toBe(false);
    expect(isFeedEligibleForRole(feed({ kind: "OHLCV_AltBar" }), "Side")).toBe(false);
    expect(isFeedEligibleForRole(feed({ kind: "aggregated" }), "Side")).toBe(false);
    expect(isFeedEligibleForRole(feed({ kind: "Tick" }), "Side")).toBe(false);
  });
});
