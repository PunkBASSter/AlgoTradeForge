"use client";

// T023 - useDebugWebSocket hook

import { useRef, useEffect, useCallback } from "react";
import { useDebugStore, type AutoStepMode } from "@/lib/stores/debug-store";
import { parseMessage } from "@/lib/events/parser";
import { createLogger } from "@/lib/utils/logger";
import type { DebugCommand, CandleData, DebugSnapshot } from "@/types/api";
import type {
  BarEventData,
  IndicatorEventData,
  OrderPlaceEventData,
  OrderFillEventData,
  OrderCancelEventData,
  OrderRejectEventData,
  PositionEventData,
  BacktestEvent,
} from "@/lib/events/types";

const log = createLogger("DebugWebSocket");

interface UseDebugWebSocketOptions {
  sessionId: string | null;
  wsUrl: string | null;
  mockEvents?: string[];
}

function barToCandle(bar: BarEventData, timeSec: number): CandleData {
  return {
    time: timeSec,
    open: bar.open,
    high: bar.high,
    low: bar.low,
    close: bar.close,
    volume: bar.volume,
  };
}

function isoToUnixSec(iso: string): number {
  return Math.floor(new Date(iso).getTime() / 1000);
}

function checkStopCondition(
  mode: AutoStepMode,
  snapshot: DebugSnapshot,
  signalSeen: boolean
): boolean {
  switch (mode.kind) {
    case "play":
      return false;
    case "next_trade":
      return snapshot.fillsThisBar > 0;
    case "next_signal":
      return signalSeen;
    case "run_to_timestamp":
      return snapshot.timestampMs >= mode.targetMs;
    case "run_to_sequence":
      return snapshot.sequenceNumber >= mode.targetSq;
  }
}

function processEvent(event: BacktestEvent) {
  const store = useDebugStore.getState();
  const timeSec = isoToUnixSec(event.ts);

  switch (event._t) {
    case "bar": {
      const bar = event.d as BarEventData;
      store.addCandle(barToCandle(bar, timeSec));
      break;
    }
    case "bar.mut": {
      const bar = event.d as BarEventData;
      store.updateCandle(barToCandle(bar, timeSec));
      break;
    }
    case "ind":
    case "ind.mut": {
      const ind = event.d as IndicatorEventData;
      store.addIndicator(ind, timeSec);
      break;
    }
    case "ord.place": {
      const data = event.d as OrderPlaceEventData;
      store.addTrade({ time: timeSec, type: "ord.place", data });
      break;
    }
    case "ord.fill": {
      const data = event.d as OrderFillEventData;
      store.addTrade({ time: timeSec, type: "ord.fill", data });
      break;
    }
    case "ord.cancel": {
      const data = event.d as OrderCancelEventData;
      store.addTrade({ time: timeSec, type: "ord.cancel", data });
      break;
    }
    case "ord.reject": {
      const data = event.d as OrderRejectEventData;
      store.addTrade({ time: timeSec, type: "ord.reject", data });
      break;
    }
    case "pos": {
      const data = event.d as PositionEventData;
      store.addTrade({ time: timeSec, type: "pos", data });
      break;
    }
    case "run.end":
      log.info("Run ended", event.d as Record<string, unknown>);
      break;
    default:
      break;
  }
}

export function useDebugWebSocket({
  sessionId,
  wsUrl,
  mockEvents,
}: UseDebugWebSocketOptions) {
  const wsRef = useRef<WebSocket | null>(null);
  const mockTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  // T064: Event buffer for requestAnimationFrame batching
  const eventBufferRef = useRef<BacktestEvent[]>([]);
  const rafIdRef = useRef<number | null>(null);
  const signalSeenRef = useRef(false);
  const store = useDebugStore();

  // Flush buffered events in a single animation frame
  const flushEventBuffer = useCallback(() => {
    const events = eventBufferRef.current;
    eventBufferRef.current = [];
    rafIdRef.current = null;
    for (const event of events) {
      processEvent(event);
    }
  }, []);

  const enqueueEvent = useCallback(
    (event: BacktestEvent) => {
      eventBufferRef.current.push(event);
      if (rafIdRef.current === null) {
        rafIdRef.current = requestAnimationFrame(flushEventBuffer);
      }
    },
    [flushEventBuffer]
  );

  // Real WebSocket mode
  useEffect(() => {
    if (!sessionId || !wsUrl || mockEvents) return;

    log.info("Connecting", { sessionId, wsUrl });
    store.setSessionState("connecting");

    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      log.info("Connected");
      store.setSessionState("active");
    };

    ws.onmessage = (msgEvent: MessageEvent) => {
      try {
        const msg = parseMessage(msgEvent.data as string);

        // Track signal events for next_signal auto-step
        if (msg.kind === "event" && msg.data._t === "sig") {
          signalSeenRef.current = true;
        }

        switch (msg.kind) {
          case "snapshot":
            store.setSnapshot(msg.data);
            if (msg.data.isExportableSubscription) {
              store.addEquityPoint(msg.data.timestampMs, msg.data.portfolioEquity);
            }
            // Generalized auto-step: check stop condition for any active mode
            if (!msg.data.sessionActive) {
              useDebugStore.getState().setAutoStep(null);
            } else {
              const autoStep = useDebugStore.getState().autoStep;
              if (autoStep !== null) {
                const shouldStop = checkStopCondition(autoStep, msg.data, signalSeenRef.current);
                signalSeenRef.current = false;
                if (shouldStop) {
                  useDebugStore.getState().setAutoStep(null);
                } else if (wsRef.current?.readyState === WebSocket.OPEN) {
                  wsRef.current.send(JSON.stringify({ command: "next" }));
                }
              }
            }
            break;
          case "error":
            log.error("Server error", { message: msg.message });
            store.setError(msg.message);
            break;
          case "ack":
            log.info("Export ack", { mutations: msg.mutations });
            break;
          case "event":
            enqueueEvent(msg.data);
            break;
        }
      } catch (err) {
        log.error("Parse error", { error: String(err) });
      }
    };

    ws.onclose = () => {
      log.info("Disconnected");
      if (store.sessionState === "active") {
        store.setError("WebSocket connection lost");
      }
    };

    ws.onerror = () => {
      log.error("WebSocket error");
      store.setError("WebSocket connection error");
    };

    return () => {
      if (rafIdRef.current !== null) cancelAnimationFrame(rafIdRef.current);
      ws.close();
      wsRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, wsUrl]);

  // Mock mode: replay events from fixture
  useEffect(() => {
    if (!sessionId || !mockEvents) return;

    log.info("Mock mode: replaying events", { count: mockEvents.length });
    store.setSessionState("active");

    let idx = 0;
    mockTimerRef.current = setInterval(() => {
      if (idx >= mockEvents.length) {
        if (mockTimerRef.current) clearInterval(mockTimerRef.current);
        return;
      }
      const line = mockEvents[idx];
      try {
        const msg = parseMessage(line);
        switch (msg.kind) {
          case "snapshot":
            store.setSnapshot(msg.data);
            if (msg.data.isExportableSubscription) {
              store.addEquityPoint(msg.data.timestampMs, msg.data.portfolioEquity);
            }
            break;
          case "error":
            store.setError(msg.message);
            break;
          case "event":
            enqueueEvent(msg.data);
            break;
          default:
            break;
        }
      } catch (err) {
        log.error("Mock parse error", { error: String(err), line });
      }
      idx++;
    }, 100);

    return () => {
      if (rafIdRef.current !== null) cancelAnimationFrame(rafIdRef.current);
      if (mockTimerRef.current) clearInterval(mockTimerRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, mockEvents]);

  const sendCommand = useCallback(
    (command: DebugCommand) => {
      if (mockEvents) {
        log.info("Mock mode: command ignored", { command: command.command });
        return;
      }
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        wsRef.current.send(JSON.stringify(command));
        log.debug("Command sent", { command: command.command });
      }
    },
    [mockEvents]
  );

  return { sendCommand };
}
