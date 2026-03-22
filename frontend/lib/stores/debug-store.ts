// T022 - Zustand debug session store

import { create } from "zustand";
import type { DebugSnapshot, CandleData } from "@/types/api";
import type { IndicatorEventData } from "@/lib/events/types";
import type { DebugTrade } from "@/components/features/charts/candlestick-chart";

export type DebugSessionState =
  | "idle"
  | "configuring"
  | "connecting"
  | "active"
  | "stopped";

export type AutoStepMode =
  | { kind: "play" }
  | { kind: "next_trade" }
  | { kind: "next_signal" }
  | { kind: "run_to_timestamp"; targetMs: number }
  | { kind: "run_to_sequence"; targetSq: number };

export interface DebugBufferPoint {
  time: number;
  value: number;
}

export interface DebugBufferMeta {
  indicatorName: string;
  bufferName: string;
  measure: string;
  chartId: number | null;
}

export interface EquityPoint {
  time: number;
  equity: number;
}

interface DebugStoreState {
  sessionState: DebugSessionState;
  sessionId: string | null;
  candles: CandleData[];
  indicatorBuffers: Map<string, DebugBufferPoint[]>;
  indicatorBufferMeta: Map<string, DebugBufferMeta>;
  trades: DebugTrade[];
  equityHistory: EquityPoint[];
  latestSnapshot: DebugSnapshot | null;
  errorMessage: string | null;
  autoStep: AutoStepMode | null;

  setSessionState: (state: DebugSessionState) => void;
  setSessionId: (id: string | null) => void;
  addCandle: (candle: CandleData) => void;
  updateCandle: (candle: CandleData) => void;
  addIndicator: (data: IndicatorEventData, time: number) => void;
  addTrade: (trade: DebugTrade) => void;
  addEquityPoint: (timestampMs: number, equity: number) => void;
  setSnapshot: (snapshot: DebugSnapshot) => void;
  setError: (message: string | null) => void;
  setAutoStep: (mode: AutoStepMode | null) => void;
  reset: () => void;
}

const initialState = {
  sessionState: "idle" as DebugSessionState,
  sessionId: null as string | null,
  candles: [] as CandleData[],
  indicatorBuffers: new Map<string, DebugBufferPoint[]>(),
  indicatorBufferMeta: new Map<string, DebugBufferMeta>(),
  trades: [] as DebugTrade[],
  equityHistory: [] as EquityPoint[],
  latestSnapshot: null as DebugSnapshot | null,
  errorMessage: null as string | null,
  autoStep: null as AutoStepMode | null,
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
      const newBuffers = new Map(state.indicatorBuffers);
      const newMeta = new Map(state.indicatorBufferMeta);

      for (const [bufferName, value] of Object.entries(data.values)) {
        const key = `${data.indicatorName}/${bufferName}`;
        const points = [...(newBuffers.get(key) ?? [])];

        // Ensure meta is set
        if (!newMeta.has(key)) {
          newMeta.set(key, {
            indicatorName: data.indicatorName,
            bufferName,
            measure: data.measure,
            chartId: data.chartIds?.[bufferName] ?? null,
          });
        }

        if (value === null) {
          // Retroactive removal: delete the point at this timestamp
          const idx = points.findIndex((p) => p.time === time);
          if (idx >= 0) {
            points.splice(idx, 1);
          }
          newBuffers.set(key, points);
          continue;
        }

        const newPoint: DebugBufferPoint = { time, value };

        // Check latest point first (hot path)
        if (points.length > 0 && points[points.length - 1].time === time) {
          points[points.length - 1] = newPoint;
        } else {
          const idx = points.findIndex((p) => p.time === time);
          if (idx >= 0) {
            points[idx] = newPoint;
          } else {
            points.push(newPoint);
          }
        }
        newBuffers.set(key, points);
      }

      return { indicatorBuffers: newBuffers, indicatorBufferMeta: newMeta };
    }),

  addTrade: (trade) =>
    set((state) => ({ trades: [...state.trades, trade] })),

  addEquityPoint: (timestampMs, equity) =>
    set((state) => {
      const timeSec = Math.floor(timestampMs / 1000);
      const last = state.equityHistory[state.equityHistory.length - 1];
      if (last && last.time === timeSec) return state;
      return { equityHistory: [...state.equityHistory, { time: timeSec, equity }] };
    }),

  setSnapshot: (latestSnapshot) => set({ latestSnapshot }),

  setError: (errorMessage) => set({ errorMessage, sessionState: "stopped" }),

  setAutoStep: (autoStep) => set({ autoStep }),

  reset: () => set({
    ...initialState,
    indicatorBuffers: new Map(),
    indicatorBufferMeta: new Map(),
    equityHistory: [],
    autoStep: null,
  }),
}));
