"use client";

// T028 - Debug page with full session lifecycle

import { useCallback, useEffect } from "react";
import dynamic from "next/dynamic";
import { useDebugStore } from "@/lib/stores/debug-store";
import { useDebugWebSocket } from "@/hooks/use-debug-websocket";
import { SessionConfigEditor } from "@/components/features/debug/session-config-editor";
import { DebugToolbar } from "@/components/features/debug/debug-toolbar";
import { DebugMetrics } from "@/components/features/debug/debug-metrics";
import { ChartSkeleton } from "@/components/features/charts/chart-skeleton";
import { useToast } from "@/components/ui/toast";
import { getClient } from "@/lib/services";
import type { StartDebugSessionRequest, DebugCommand } from "@/types/api";

const CandlestickChart = dynamic(
  () =>
    import("@/components/features/charts/candlestick-chart").then(
      (m) => m.CandlestickChart
    ),
  { ssr: false, loading: () => <ChartSkeleton /> }
);

export default function DebugPage() {
  const store = useDebugStore();
  const { toast } = useToast();
  const client = getClient();

  // T062: Navigation-away confirmation for active sessions
  useEffect(() => {
    const handleBeforeUnload = (e: BeforeUnloadEvent) => {
      if (
        store.sessionState === "active" ||
        store.sessionState === "connecting"
      ) {
        e.preventDefault();
      }
    };
    window.addEventListener("beforeunload", handleBeforeUnload);
    return () => window.removeEventListener("beforeunload", handleBeforeUnload);
  }, [store.sessionState]);

  const isMockMode = process.env.NEXT_PUBLIC_MOCK_MODE === "true";
  const mockEvents =
    isMockMode && store.sessionId
      ? ("getMockDebugEvents" in client
          ? (client as { getMockDebugEvents(): string[] }).getMockDebugEvents()
          : undefined)
      : undefined;

  const { sendCommand } = useDebugWebSocket({
    sessionId: store.sessionId,
    wsUrl:
      !isMockMode && store.sessionId
        ? client.getDebugWebSocketUrl(store.sessionId)
        : null,
    mockEvents,
  });

  const handleStart = useCallback(
    async (config: StartDebugSessionRequest) => {
      const s = useDebugStore.getState();
      try {
        s.reset();
        s.setSessionState("configuring");
        const session = await client.createDebugSession(config);
        const s2 = useDebugStore.getState();
        s2.setSessionId(session.sessionId);
        s2.setSessionState("connecting");
      } catch (err) {
        toast(String(err), "error");
        useDebugStore.getState().setSessionState("idle");
      }
    },
    [client, toast]
  );

  const handleStop = useCallback(async () => {
    const s = useDebugStore.getState();
    if (s.sessionId) {
      try {
        await client.deleteDebugSession(s.sessionId);
      } catch {
        // Session may already be gone
      }
    }
    useDebugStore.getState().reset();
  }, [client]);

  const handleCommand = useCallback(
    (command: DebugCommand) => {
      sendCommand(command);
    },
    [sendCommand]
  );

  const isActive =
    store.sessionState === "active" || store.sessionState === "connecting";

  return (
    <div className="p-6 space-y-4">
      <h1 className="text-xl font-bold text-text-primary">Debug Session</h1>

      {store.sessionState === "idle" && (
        <SessionConfigEditor onStart={handleStart} />
      )}

      {store.sessionState === "configuring" && (
        <div className="flex items-center gap-2 text-text-secondary">
          <div className="w-4 h-4 border-2 border-accent-blue border-t-transparent rounded-full animate-spin" />
          Starting session...
        </div>
      )}

      {store.sessionState === "connecting" && (
        <div className="flex items-center gap-2 text-text-secondary">
          <div className="w-4 h-4 border-2 border-accent-blue border-t-transparent rounded-full animate-spin" />
          Connecting to WebSocket...
        </div>
      )}

      {isActive && (
        <>
          <DebugToolbar
            onCommand={handleCommand}
            onStop={handleStop}
            disabled={store.sessionState !== "active"}
          />

          <div className="grid grid-cols-1 xl:grid-cols-[1fr_320px] gap-4">
            <CandlestickChart
              candles={store.candles}
              debugIndicators={store.indicators}
              debugTrades={store.trades}
            />
            <DebugMetrics snapshot={store.latestSnapshot} />
          </div>
        </>
      )}

      {store.sessionState === "stopped" && (
        <div className="space-y-4">
          {store.errorMessage && (
            <div className="p-4 bg-bg-panel border border-accent-red rounded-lg">
              <p className="text-accent-red text-sm">{store.errorMessage}</p>
            </div>
          )}
          <SessionConfigEditor onStart={handleStart} />
        </div>
      )}
    </div>
  );
}
