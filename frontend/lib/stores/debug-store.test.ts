import { describe, it, expect, beforeEach } from "vitest";
import { useDebugStore } from "./debug-store";

describe("debug-store", () => {
  beforeEach(() => {
    // Reset to initial state before each test
    useDebugStore.getState().reset();
  });

  it("starts in idle state", () => {
    const state = useDebugStore.getState();
    expect(state.sessionState).toBe("idle");
    expect(state.sessionId).toBeNull();
    expect(state.candles).toEqual([]);
    expect(state.trades).toEqual([]);
    expect(state.latestSnapshot).toBeNull();
    expect(state.errorMessage).toBeNull();
  });

  it("sets session state", () => {
    useDebugStore.getState().setSessionState("active");
    expect(useDebugStore.getState().sessionState).toBe("active");
  });

  it("sets session id", () => {
    useDebugStore.getState().setSessionId("test-123");
    expect(useDebugStore.getState().sessionId).toBe("test-123");
  });

  it("adds candles", () => {
    const candle = { time: 1000, open: 100, high: 110, low: 90, close: 105, volume: 500 };
    useDebugStore.getState().addCandle(candle);
    expect(useDebugStore.getState().candles).toEqual([candle]);
  });

  it("updates existing candle by time", () => {
    const candle1 = { time: 1000, open: 100, high: 110, low: 90, close: 105, volume: 500 };
    const candle2 = { time: 1000, open: 100, high: 115, low: 90, close: 112, volume: 600 };
    useDebugStore.getState().addCandle(candle1);
    useDebugStore.getState().updateCandle(candle2);
    expect(useDebugStore.getState().candles).toEqual([candle2]);
  });

  it("appends candle via updateCandle when time not found", () => {
    const candle1 = { time: 1000, open: 100, high: 110, low: 90, close: 105, volume: 500 };
    const candle2 = { time: 2000, open: 105, high: 120, low: 100, close: 118, volume: 700 };
    useDebugStore.getState().addCandle(candle1);
    useDebugStore.getState().updateCandle(candle2);
    expect(useDebugStore.getState().candles).toHaveLength(2);
  });

  it("sets error and transitions to stopped", () => {
    useDebugStore.getState().setSessionState("active");
    useDebugStore.getState().setError("Connection lost");
    const state = useDebugStore.getState();
    expect(state.errorMessage).toBe("Connection lost");
    expect(state.sessionState).toBe("stopped");
  });

  it("resets to initial state", () => {
    useDebugStore.getState().setSessionState("active");
    useDebugStore.getState().setSessionId("abc");
    useDebugStore.getState().addCandle({ time: 1, open: 1, high: 1, low: 1, close: 1, volume: 1 });
    useDebugStore.getState().reset();
    const state = useDebugStore.getState();
    expect(state.sessionState).toBe("idle");
    expect(state.sessionId).toBeNull();
    expect(state.candles).toEqual([]);
  });

  it("adds indicators grouped by name", () => {
    const ind = { indicatorName: "SMA", measure: "price" as const, values: { sma20: 100 } };
    useDebugStore.getState().addIndicator(ind, 1000);
    useDebugStore.getState().addIndicator(ind, 2000);

    const indicators = useDebugStore.getState().indicators;
    expect(indicators.get("SMA")).toHaveLength(2);
    expect(indicators.get("SMA")![0].time).toBe(1000);
    expect(indicators.get("SMA")![1].time).toBe(2000);
  });
});
