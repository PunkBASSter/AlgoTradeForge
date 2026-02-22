// T041 - MetricsPanel component

import { toTitleCase, formatNumber, formatPercent } from "@/lib/utils/format";

interface MetricsPanelProps {
  metrics: Record<string, number>;
}

export function MetricsPanel({ metrics }: MetricsPanelProps) {
  const entries = Object.entries(metrics);

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel p-4">
      <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
        Metrics
      </h3>
      <div className="grid grid-cols-2 gap-x-6 gap-y-2">
        {entries.map(([key, value]) => (
          <div key={key} className="flex justify-between gap-2">
            <span className="text-sm text-text-secondary truncate">
              {toTitleCase(key)}
            </span>
            <span className="text-sm font-medium text-text-primary whitespace-nowrap">
              {key.endsWith("Pct") ? formatPercent(value) : formatNumber(value)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
