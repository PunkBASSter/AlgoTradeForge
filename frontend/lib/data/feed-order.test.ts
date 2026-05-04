import { describe, it, expect } from "vitest";
import { compareFeed, unionFeedColumns } from "./feed-order";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

const tb = (id: string, interval: string): FeedCatalogEntry => ({
  id, kind: "OHLCV_TimeBar", interval, type_code: null, threshold_value: null, sidecar: null,
});
const alt = (id: string, type: string, threshold: number, sidecar: string | null = null): FeedCatalogEntry => ({
  id, kind: "OHLCV_AltBar", interval: null, type_code: type, threshold_value: threshold, sidecar,
});
const tick: FeedCatalogEntry = {
  id: "ticks", kind: "Tick", interval: null, type_code: null, threshold_value: null, sidecar: null,
};
const side = (id: string): FeedCatalogEntry => ({
  id, kind: "Side", interval: null, type_code: null, threshold_value: null, sidecar: null,
});

describe("compareFeed", () => {
  it("orders TimeBar < AltBar < Tick < Side regardless of intra-bucket fields", () => {
    const items = [side("funding-rate"), tick, alt("EqV_1m_1000", "EqV", 1000), tb("1m", "1m")];
    items.sort(compareFeed);
    expect(items.map((f) => f.id)).toEqual(["1m", "EqV_1m_1000", "ticks", "funding-rate"]);
  });

  it("orders alt bars by type_code then threshold ascending (TRD §3.3)", () => {
    const items = [
      alt("EqV_1m_5000", "EqV", 5000),
      alt("EqI_ticks_500", "EqI", 500),
      alt("EqV_1m_1000", "EqV", 1000),
    ];
    items.sort(compareFeed);
    expect(items.map((f) => f.id)).toEqual(["EqI_ticks_500", "EqV_1m_1000", "EqV_1m_5000"]);
  });

  it("orders side feeds lexically by id", () => {
    const items = [side("open-interest"), side("funding-rate"), side("liquidations")];
    items.sort(compareFeed);
    expect(items.map((f) => f.id)).toEqual(["funding-rate", "liquidations", "open-interest"]);
  });

  it("orders time bars by interval duration ascending, not lexically", () => {
    // Lex order would put "1d" before "1h" before "1m" (alphabetic 'd' < 'h' < 'm') — the
    // opposite of what users expect. Verify the new duration parser produces 1m → 1h → 1d.
    const items = [tb("1d", "1d"), tb("1m", "1m"), tb("1h", "1h"), tb("4h", "4h"), tb("15m", "15m"), tb("5m", "5m")];
    items.sort(compareFeed);
    expect(items.map((f) => f.id)).toEqual(["1m", "5m", "15m", "1h", "4h", "1d"]);
  });
});

describe("unionFeedColumns", () => {
  it("returns the ordered union of feeds across visible assets", () => {
    const a1: AssetCatalogEntry = {
      exchange: "binance", symbol: "BTCUSDT_perp", display_name: "BTCUSDT", type: "CryptoPerpetual",
      feeds: [tb("1m", "1m"), alt("EqV_1m_1000", "EqV", 1000), tick],
    };
    const a2: AssetCatalogEntry = {
      exchange: "binance", symbol: "ETHUSDT_perp", display_name: "ETHUSDT", type: "CryptoPerpetual",
      feeds: [tb("1m", "1m"), tb("5m", "5m"), side("funding-rate")],
    };
    const cols = unionFeedColumns([a1, a2]);
    expect(cols.map((c) => c.id)).toEqual(["1m", "5m", "EqV_1m_1000", "ticks", "funding-rate"]);
  });

  it("returns empty for empty asset list (does not throw)", () => {
    expect(unionFeedColumns([])).toEqual([]);
  });

  it("first occurrence wins for duplicate feed ids — same schema invariant", () => {
    const a1: AssetCatalogEntry = {
      exchange: "binance", symbol: "A", display_name: "A", type: "x",
      feeds: [alt("EqV_1m_1000", "EqV", 1000, "EqV_1m_1000.flow")],
    };
    const a2: AssetCatalogEntry = {
      exchange: "binance", symbol: "B", display_name: "B", type: "x",
      feeds: [alt("EqV_1m_1000", "EqV", 1000, null)],   // somehow no sidecar — wrong but test the rule
    };
    const cols = unionFeedColumns([a1, a2]);
    expect(cols).toHaveLength(1);
    expect(cols[0].sidecar).toBe("EqV_1m_1000.flow");   // first occurrence wins
  });
});
