// T027 - Debug metrics panel displaying snapshot data

import type { DebugSnapshot } from "@/types/api";
import { formatNumber } from "@/lib/utils/format";

interface DebugMetricsProps {
  snapshot: DebugSnapshot | null;
}

function formatTimestamp(ms: number): string {
  return new Date(ms).toISOString().replace("T", " ").replace("Z", " UTC");
}

export function DebugMetrics({ snapshot }: DebugMetricsProps) {
  if (!snapshot) {
    return (
      <div className="p-4 bg-bg-panel rounded-lg border border-border-default">
        <p className="text-text-muted text-sm">Waiting for snapshot...</p>
      </div>
    );
  }

  const metrics = [
    {
      label: "Session Active",
      value: snapshot.sessionActive ? "Yes" : "No",
      color: snapshot.sessionActive ? "text-accent-green" : "text-accent-red",
    },
    { label: "Sequence", value: snapshot.sequenceNumber.toString() },
    { label: "Timestamp", value: formatTimestamp(snapshot.timestampMs) },
    {
      label: "Portfolio Equity",
      value: formatNumber(snapshot.portfolioEquity),
    },
    { label: "Fills This Bar", value: snapshot.fillsThisBar.toString() },
    {
      label: "Subscription Index",
      value: snapshot.subscriptionIndex.toString(),
    },
  ];

  return (
    <div className="p-4 bg-bg-panel rounded-lg border border-border-default">
      <h3 className="text-sm font-semibold text-text-secondary mb-3">
        Session Metrics
      </h3>
      <div className="grid grid-cols-2 gap-x-6 gap-y-2">
        {metrics.map((m) => (
          <div key={m.label} className="flex justify-between">
            <span className="text-sm text-text-muted">{m.label}</span>
            <span className={`text-sm font-mono ${m.color ?? "text-text-primary"}`}>
              {m.value}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
