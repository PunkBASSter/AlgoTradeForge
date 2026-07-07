import { describe, expect, it } from "vitest";
import { exchangeSymbolOf, mapCatalogFeedToCoverage } from "./coverage-mapping";
import type { FeedCatalogEntry } from "@/types/data-tab";

function makeEntry(overrides: Partial<FeedCatalogEntry>): FeedCatalogEntry {
  return {
    id: "1h",
    kind: "OHLCV_TimeBar",
    interval: "1h",
    type_code: null,
    threshold_value: null,
    sidecar: null,
    ...overrides,
  };
}

describe("exchangeSymbolOf", () => {
  it("strips _perp suffix for perpetual/future assets", () => {
    expect(exchangeSymbolOf({ symbol: "BTCUSDT_perp" })).toBe("BTCUSDT");
    expect(exchangeSymbolOf({ symbol: "ETHUSDT_perp" })).toBe("ETHUSDT");
  });

  it("passes spot symbols through unchanged", () => {
    expect(exchangeSymbolOf({ symbol: "BTCUSDT" })).toBe("BTCUSDT");
    expect(exchangeSymbolOf({ symbol: "AAPL" })).toBe("AAPL");
  });
});

describe("mapCatalogFeedToCoverage", () => {
  describe("OHLCV_TimeBar", () => {
    it("maps to candles with feed.id as interval", () => {
      expect(mapCatalogFeedToCoverage(makeEntry({ id: "1h", kind: "OHLCV_TimeBar" }))).toEqual({
        feedName: "candles",
        interval: "1h",
      });
    });

    it("uses feed.id regardless of the interval field", () => {
      expect(
        mapCatalogFeedToCoverage(
          makeEntry({ id: "5m", kind: "OHLCV_TimeBar", interval: "5m" }),
        ),
      ).toEqual({ feedName: "candles", interval: "5m" });
    });
  });

  describe("Side", () => {
    it("returns feed.id as feedName with empty-string interval sentinel", () => {
      expect(
        mapCatalogFeedToCoverage(
          makeEntry({ id: "funding-rate", kind: "Side", interval: null }),
        ),
      ).toEqual({ feedName: "funding-rate", interval: "" });
    });

    it("preserves combined feed ids (e.g. feed_name_interval form)", () => {
      expect(
        mapCatalogFeedToCoverage(
          makeEntry({ id: "ls-ratio-global", kind: "Side", interval: null }),
        ),
      ).toEqual({ feedName: "ls-ratio-global", interval: "" });
    });
  });

  describe("AltBar / Tick / aggregated → null", () => {
    it("OHLCV_AltBar returns null", () => {
      expect(
        mapCatalogFeedToCoverage(
          makeEntry({ id: "EqV_1m", kind: "OHLCV_AltBar", interval: null, type_code: "EqV" }),
        ),
      ).toBeNull();
    });

    it("Tick returns { feedName: 'ticks', interval: '' }", () => {
      expect(
        mapCatalogFeedToCoverage(makeEntry({ id: "trades", kind: "Tick", interval: null })),
      ).toEqual({ feedName: "ticks", interval: "" });
    });

    it("aggregated returns null", () => {
      expect(
        mapCatalogFeedToCoverage(
          makeEntry({ id: "EqV_1m_1M", kind: "aggregated", interval: null }),
        ),
      ).toBeNull();
    });
  });
});
