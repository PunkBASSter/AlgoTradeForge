"use client";

// T028 - Debug page with full session lifecycle

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import dynamic from "next/dynamic";
import { useDebugStore } from "@/lib/stores/debug-store";
import { useDebugWebSocket } from "@/hooks/use-debug-websocket";
import { SessionConfigEditor } from "@/components/features/debug/session-config-editor";
import { SessionSettingsCard } from "@/components/features/debug/session-settings-card";
import { DebugToolbar } from "@/components/features/debug/debug-toolbar";
import { DebugMetrics } from "@/components/features/debug/debug-metrics";
import { OrderLog } from "@/components/features/debug/order-log";
import { ChartSkeleton } from "@/components/features/charts/chart-skeleton";
import { useToast } from "@/components/ui/toast";
import { getClient } from "@/lib/services";
import type { StartDebugSessionRequest, DebugCommand } from "@/types/api";
import { SESSION_KEYS } from "@/lib/constants";
import { reduceOrders } from "@/lib/utils/orders";

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
        s.setInitialConfig(config);
        const session = await client.createDebugSession(config);
        const s2 = useDebugStore.getState();
        s2.setSessionId(session.sessionId);
        s2.setLogFolderPath(session.logFolderPath);
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
    const autostart = sessionStorage.getItem(SESSION_KEYS.DEBUG_AUTOSTART);
    if (!autostart) return null;
    sessionStorage.removeItem(SESSION_KEYS.DEBUG_AUTOSTART);
    const stored = sessionStorage.getItem(SESSION_KEYS.DEBUG_CONFIG);
    if (!stored) return null;
    sessionStorage.removeItem(SESSION_KEYS.DEBUG_CONFIG);
    try {
      const config = JSON.parse(stored) as StartDebugSessionRequest;
      if (config.strategyName && config.dataSubscriptions) return config;
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

  const orderRows = useMemo(() => reduceOrders(store.trades), [store.trades]);

  const handleCopyLogPath = useCallback(() => {
    if (!store.logFolderPath) return;
    navigator.clipboard.writeText(store.logFolderPath);
    toast("Log path copied", "success");
  }, [store.logFolderPath, toast]);

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-baseline justify-between gap-4 flex-wrap">
        <h1 className="text-xl font-bold text-text-primary">Debug Session</h1>
        {store.logFolderPath && (
          <div
            className="flex items-center gap-2 text-xs text-text-muted bg-bg-panel border border-border-default rounded px-2 py-1 max-w-full"
            data-testid="log-path-banner"
          >
            <span className="font-medium uppercase tracking-wider">Logs:</span>
            <span
              className="font-mono text-text-secondary truncate"
              title={store.logFolderPath}
            >
              {store.logFolderPath}
            </span>
            <button
              onClick={handleCopyLogPath}
              className="p-1 rounded hover:bg-bg-surface text-text-muted hover:text-text-primary transition-colors flex-shrink-0"
              title="Copy log path to clipboard"
              aria-label="Copy log path"
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 16 16"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.5"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <rect x="5.5" y="5.5" width="8" height="8" rx="1" />
                <path d="M10.5 5.5V3.5a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2" />
              </svg>
            </button>
          </div>
        )}
      </div>

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
          {store.initialConfig && (
            <SessionSettingsCard config={store.initialConfig} />
          )}
          <DebugToolbar
            onCommand={handleCommand}
            onStop={handleStop}
            disabled={store.sessionState !== "active"}
          />

          <div className="grid grid-cols-1 xl:grid-cols-[1fr_400px] gap-4">
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
            <div className="flex flex-col gap-2 min-h-0">
              <DebugMetrics snapshot={store.latestSnapshot} />
              <OrderLog orders={orderRows} />
            </div>
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
