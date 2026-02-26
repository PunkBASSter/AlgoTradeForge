// T022 - Zustand debug session store

import { create } from "zustand";
import type { DebugSnapshot, CandleData } from "@/types/api";
import type { IndicatorEventData, OrderPlaceEventData, OrderFillEventData, PositionEventData } from "@/lib/events/types";
import type { DebugTrade } from "@/components/features/charts/candlestick-chart";

export type DebugSessionState =
  | "idle"
  | "configuring"
  | "connecting"
  | "active"
  | "stopped";

export interface DebugIndicatorPoint {
  time: number;
  indicatorName: string;
  measure: string;
  values: Record<string, number | null>;
}

interface DebugStoreState {
  sessionState: DebugSessionState;
  sessionId: string | null;
  candles: CandleData[];
  indicators: Map<string, DebugIndicatorPoint[]>;
  trades: DebugTrade[];
  latestSnapshot: DebugSnapshot | null;
  errorMessage: string | null;

  setSessionState: (state: DebugSessionState) => void;
  setSessionId: (id: string | null) => void;
  addCandle: (candle: CandleData) => void;
  updateCandle: (candle: CandleData) => void;
  addIndicator: (data: IndicatorEventData, time: number) => void;
  addTrade: (trade: DebugTrade) => void;
  setSnapshot: (snapshot: DebugSnapshot) => void;
  setError: (message: string | null) => void;
  reset: () => void;
}

const initialState = {
  sessionState: "idle" as DebugSessionState,
  sessionId: null as string | null,
  candles: [] as CandleData[],
  indicators: new Map<string, DebugIndicatorPoint[]>(),
  trades: [] as DebugTrade[],
  latestSnapshot: null as DebugSnapshot | null,
  errorMessage: null as string | null,
};

export const useDebugStore = create<DebugStoreState>((set) => ({
  ...initialState,

  setSessionState: (sessionState) => set({ sessionState }),
  setSessionId: (sessionId) => set({ sessionId }),

  addCandle: (candle) =>
    set((state) => {
      const updated = [...state.candles];
      const idx = updated.findIndex((c) => c.time === candle.time);
      if (idx >= 0) {
        updated[idx] = candle;
      } else {
        updated.push(candle);
      }
      return { candles: updated };
    }),

  updateCandle: (candle) =>
    set((state) => {
      const updated = [...state.candles];
      const idx = updated.findIndex((c) => c.time === candle.time);
      if (idx >= 0) {
        updated[idx] = candle;
      } else {
        updated.push(candle);
      }
      return { candles: updated };
    }),

  addIndicator: (data, time) =>
    set((state) => {
      const newMap = new Map(state.indicators);
      const points = [...(newMap.get(data.indicatorName) ?? [])];
      const allNull = Object.values(data.values).every((v) => v === null);

      if (allNull) {
        // Retroactive removal: delete the point at this timestamp
        const idx = points.findIndex((p) => p.time === time);
        if (idx >= 0) {
          points.splice(idx, 1);
        }
        newMap.set(data.indicatorName, points);
        return { indicators: newMap };
      }

      const newPoint: DebugIndicatorPoint = {
        time,
        indicatorName: data.indicatorName,
        measure: data.measure,
        values: data.values,
      };

      // Check latest point first (hot path)
      if (points.length > 0 && points[points.length - 1].time === time) {
        points[points.length - 1] = newPoint;
      } else {
        // Past-timestamp update: find and replace existing point
        const idx = points.findIndex((p) => p.time === time);
        if (idx >= 0) {
          points[idx] = newPoint;
        } else {
          points.push(newPoint);
        }
      }
      newMap.set(data.indicatorName, points);
      return { indicators: newMap };
    }),

  addTrade: (trade) =>
    set((state) => ({ trades: [...state.trades, trade] })),

  setSnapshot: (latestSnapshot) => set({ latestSnapshot }),

  setError: (errorMessage) => set({ errorMessage, sessionState: "stopped" }),

  reset: () => set({ ...initialState, indicators: new Map() }),
}));
