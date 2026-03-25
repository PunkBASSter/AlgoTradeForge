"use client";

// T028 - Debug page with full session lifecycle

import { useCallback, useEffect, useRef, useState } from "react";
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

const ChartStack = dynamic(
  () =>
    import("@/components/features/charts/chart-stack").then(
      (m) => m.ChartStack
    ),
  { ssr: false, loading: () => <ChartSkeleton /> }
);

const PnlChart = dynamic(
  () =>
    import("@/components/features/charts/pnl-chart").then(
      (m) => m.PnlChart
    ),
  { ssr: false }
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
        // Clean up previous session on server before creating a new one,
        // otherwise zombie sessions accumulate and exhaust the session store.
        if (s.sessionId) {
          try {
            await client.deleteDebugSession(s.sessionId);
          } catch {
            // Session may already be gone
          }
        }
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

  // Read autostart config synchronously so SessionConfigEditor never races for the key
  const [autostartConfig] = useState<StartDebugSessionRequest | null>(() => {
    if (typeof window === "undefined") return null;
    const autostart = sessionStorage.getItem("debug-session-autostart");
    if (!autostart) return null;
    sessionStorage.removeItem("debug-session-autostart");
    const stored = sessionStorage.getItem("debug-session-config");
    if (!stored) return null;
    sessionStorage.removeItem("debug-session-config");
    try {
      const config = JSON.parse(stored) as StartDebugSessionRequest;
      if (config.strategyName && config.dataSubscription) return config;
    } catch { /* invalid JSON */ }
    return null;
  });

  // Fire handleStart once on mount when autostart config is present
  const autostartFired = useRef(false);
  useEffect(() => {
    if (autostartConfig && !autostartFired.current) {
      autostartFired.current = true;
      handleStart(autostartConfig);
    }
  }, [autostartConfig, handleStart]);

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

      {store.sessionState === "idle" && !autostartConfig && (
        <SessionConfigEditor onStart={handleStart} />
      )}

      {store.sessionState === "idle" && autostartConfig && (
        <div className="flex items-center gap-2 text-text-secondary">
          <div className="w-4 h-4 border-2 border-accent-blue border-t-transparent rounded-full animate-spin" />
          Starting session...
        </div>
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
            <div className="space-y-2">
              <ChartStack
                candles={store.candles}
                indicatorBuffers={store.indicatorBuffers}
                indicatorBufferMeta={store.indicatorBufferMeta}
                debugTrades={store.trades}
              />
              {store.equityHistory.length > 0 && (
                <PnlChart equityHistory={store.equityHistory} />
              )}
            </div>
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
