// T041 - MetricsPanel component

import { toTitleCase, formatNumber, formatPercent } from "@/lib/utils/format";

interface MetricsPanelProps {
  metrics: Record<string, number>;
}

export function MetricsPanel({ metrics }: MetricsPanelProps) {
  const fitnessScore = metrics.fitness;
  const entries = Object.entries(metrics).filter(([key]) => key !== "fitness");

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel p-4">
      <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
        Metrics
      </h3>
      {fitnessScore != null && (
        <div className="flex justify-between items-center mb-3 pb-3 border-b border-border-default">
          <span className="text-sm font-semibold text-text-secondary">Fitness Score</span>
          <span className="text-base font-bold text-accent-primary">{formatNumber(fitnessScore, 4)}</span>
        </div>
      )}
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
