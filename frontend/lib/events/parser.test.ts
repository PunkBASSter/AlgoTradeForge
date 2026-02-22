import { describe, it, expect } from "vitest";
import { parseMessage } from "./parser";

describe("parseMessage", () => {
  it("parses a snapshot server message", () => {
    const raw = JSON.stringify({
      type: "snapshot",
      sessionActive: true,
      sequenceNumber: 42,
      timestampMs: 1700000000000,
      subscriptionIndex: 0,
      isExportableSubscription: false,
      fillsThisBar: 0,
      portfolioEquity: 10000,
    });

    const result = parseMessage(raw);
    expect(result.kind).toBe("snapshot");
    if (result.kind === "snapshot") {
      expect(result.data.sequenceNumber).toBe(42);
      expect(result.data.portfolioEquity).toBe(10000);
    }
  });

  it("parses an error server message", () => {
    const raw = JSON.stringify({ type: "error", message: "something broke" });
    const result = parseMessage(raw);
    expect(result).toEqual({ kind: "error", message: "something broke" });
  });

  it("parses a set_export_ack message", () => {
    const raw = JSON.stringify({ type: "set_export_ack", mutations: true });
    const result = parseMessage(raw);
    expect(result).toEqual({ kind: "ack", mutations: true });
  });

  it("parses a backtest event with _t field", () => {
    const raw = JSON.stringify({
      ts: "2025-01-01T00:00:00Z",
      sq: 1,
      _t: "bar",
      src: "BTCUSDT",
      d: { assetName: "BTCUSDT", timeFrame: "00:15:00", open: 100, high: 110, low: 95, close: 105, volume: 500 },
    });

    const result = parseMessage(raw);
    expect(result.kind).toBe("event");
    if (result.kind === "event") {
      expect(result.data._t).toBe("bar");
      expect(result.data.sq).toBe(1);
    }
  });

  it("throws on non-object JSON", () => {
    expect(() => parseMessage('"hello"')).toThrow("Expected JSON object");
  });

  it("throws on invalid JSON", () => {
    expect(() => parseMessage("not json")).toThrow();
  });

  it("throws on object without type or _t field", () => {
    expect(() => parseMessage('{"foo": "bar"}')).toThrow(
      "Cannot discriminate message"
    );
  });

  it("throws on unknown server message type", () => {
    expect(() =>
      parseMessage(JSON.stringify({ type: "unknown_type" }))
    ).toThrow("Unknown server message type");
  });
});
