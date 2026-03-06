"use client";

import React from "react";
import { useRouter } from "next/navigation";
import { useLiveSessionDetail, useStopLiveSession } from "@/hooks/use-live-sessions";
import { StatusBadge } from "@/components/ui/status-badge";
import { StatItem } from "@/components/ui/stat-item";
import { Skeleton } from "@/components/ui/skeleton";

export default function LiveSessionPage({
  params,
}: {
  params: Promise<{ exchange: string; id: string }>;
}) {
  const { exchange, id } = React.use(params);
  const router = useRouter();

  const { data: session, isLoading, error } = useLiveSessionDetail(id);
  const stopMutation = useStopLiveSession();

  const handleTerminate = () => {
    if (!confirm("Stop this live trading session? This cannot be undone.")) return;
    const strategy = session?.strategyName ?? "all";
    stopMutation.mutate(id, {
      onSuccess: () => router.push(`/${encodeURIComponent(strategy)}/live`),
    });
  };

  if (error) {
    return (
      <div className="p-6 flex flex-col items-center justify-center gap-4">
        <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
          <h2 className="text-lg font-semibold text-accent-red mb-2">
            Failed to load session
          </h2>
          <p className="text-sm text-text-secondary">{error.message}</p>
        </div>
      </div>
    );
  }

  if (isLoading || !session) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <Skeleton variant="rect" height="80px" />
        <Skeleton variant="rect" height="200px" />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      {/* Title + Terminate */}
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-bold text-text-primary">
            Live Session: {session.strategyName}
            <span className="ml-2 text-sm font-normal text-text-muted">
              v{session.strategyVersion}
            </span>
          </h1>
          <p className="text-sm text-text-secondary mt-1">
            {session.assetName} / {decodeURIComponent(exchange)}
          </p>
        </div>
        <button
          type="button"
          onClick={handleTerminate}
          disabled={stopMutation.isPending}
          className="shrink-0 px-3 py-1.5 rounded-md text-sm font-medium bg-accent-red text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {stopMutation.isPending ? "Stopping..." : "Terminate Session"}
        </button>
      </div>

      {/* Session Info */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
          <StatItem label="Status" value={<StatusBadge status={session.status} />} />
          <StatItem
            label="Session ID"
            value={
              <span className="text-sm font-mono">{session.sessionId.substring(0, 12)}...</span>
            }
          />
          <StatItem label="Account" value={session.accountName} />
          <StatItem
            label="Started"
            value={new Date(session.startedAt).toLocaleString()}
          />
          <StatItem label="Exchange" value={session.exchange} />
          <StatItem label="Asset" value={session.assetName} />
          <StatItem label="Strategy" value={session.strategyName} />
        </div>
      </div>

      {/* Placeholder sections for future data */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
          Market Data
        </h2>
        <p className="text-sm text-text-muted">
          Bid/Ask quotes will appear here when the session is connected to the exchange.
        </p>
      </div>

      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
          Recent Orders
        </h2>
        <p className="text-sm text-text-muted">
          Order history will appear here once the session places trades.
        </p>
      </div>

      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
          Account Funds
        </h2>
        <p className="text-sm text-text-muted">
          Account balance and equity will appear here when connected.
        </p>
      </div>
    </div>
  );
}
