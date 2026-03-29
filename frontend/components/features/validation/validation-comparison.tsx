"use client";

import { VerdictBadge } from "./verdict-badge";
import { formatDuration, formatNumber } from "@/lib/utils/format";
import type { ValidationRun } from "@/types/validation";

const CATEGORY_LABELS: Record<string, string> = {
  Data: "Data Sufficiency",
  Stats: "Statistical Significance",
  Params: "Parameter Landscape",
  WFO: "Walk-Forward Optimization",
  WFM: "Walk-Forward Matrix",
  MC: "Monte Carlo",
  SubPeriod: "Sub-Period Consistency",
};

const CATEGORY_ORDER = ["Data", "Stats", "Params", "WFO", "WFM", "MC", "SubPeriod"];

function scoreColor(score: number): string {
  if (score >= 70) return "bg-green-500/20 text-green-400";
  if (score >= 40) return "bg-yellow-500/20 text-yellow-400";
  return "bg-red-500/20 text-red-400";
}

function bestInRow(values: (number | undefined)[]): number {
  let best = -1;
  let bestVal = -Infinity;
  for (let i = 0; i < values.length; i++) {
    if (values[i] !== undefined && values[i]! > bestVal) {
      bestVal = values[i]!;
      best = i;
    }
  }
  return best;
}

interface ValidationComparisonProps {
  validations: ValidationRun[];
}

export function ValidationComparison({ validations }: ValidationComparisonProps) {
  if (validations.length === 0) {
    return (
      <div className="p-6 text-center text-text-muted">
        No validations selected for comparison.
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Score gauges row */}
      <div className="grid gap-4" style={{ gridTemplateColumns: `repeat(${validations.length}, 1fr)` }}>
        {validations.map((v) => (
          <div key={v.id} className="rounded-lg border border-border-default bg-bg-panel p-4 text-center space-y-2">
            <div className="text-xs text-text-muted font-mono">{v.id.slice(0, 8)}</div>
            <div className="text-sm font-medium text-text-primary">{v.strategyName}</div>
            <div className="text-xs text-text-muted">{v.thresholdProfileName}</div>
            <div className="text-3xl font-bold text-text-primary">{v.compositeScore.toFixed(0)}</div>
            <VerdictBadge verdict={v.verdict} size="lg" />
          </div>
        ))}
      </div>

      {/* Category scores comparison table */}
      <div className="rounded-lg border border-border-default overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-bg-tertiary/50 border-b border-border-default">
              <th className="p-3 text-left text-text-muted font-medium">Category</th>
              {validations.map((v) => (
                <th key={v.id} className="p-3 text-center text-text-muted font-medium">
                  {v.id.slice(0, 8)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {CATEGORY_ORDER.map((cat) => {
              const values = validations.map((v) => v.categoryScores[cat]);
              const bestIdx = bestInRow(values);
              return (
                <tr key={cat} className="border-b border-border-default/50">
                  <td className="p-3 text-text-secondary">{CATEGORY_LABELS[cat] ?? cat}</td>
                  {validations.map((v, idx) => {
                    const score = v.categoryScores[cat] ?? 0;
                    const isBest = idx === bestIdx && validations.length > 1;
                    return (
                      <td key={v.id} className="p-3 text-center">
                        <span className={`inline-block px-2 py-0.5 rounded text-xs font-mono ${scoreColor(score)} ${isBest ? "ring-1 ring-green-400/60" : ""}`}>
                          {score.toFixed(0)}
                        </span>
                      </td>
                    );
                  })}
                </tr>
              );
            })}
            {/* Composite score row */}
            <tr className="bg-bg-tertiary/30 font-semibold">
              <td className="p-3 text-text-primary">Composite Score</td>
              {validations.map((v, idx) => {
                const isBest = idx === bestInRow(validations.map((x) => x.compositeScore));
                return (
                  <td key={v.id} className="p-3 text-center">
                    <span className={`inline-block px-2 py-0.5 rounded text-sm font-mono ${scoreColor(v.compositeScore)} ${isBest ? "ring-1 ring-green-400/60" : ""}`}>
                      {v.compositeScore.toFixed(1)}
                    </span>
                  </td>
                );
              })}
            </tr>
          </tbody>
        </table>
      </div>

      {/* Key metrics comparison */}
      <div className="rounded-lg border border-border-default overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-bg-tertiary/50 border-b border-border-default">
              <th className="p-3 text-left text-text-muted font-medium">Metric</th>
              {validations.map((v) => (
                <th key={v.id} className="p-3 text-center text-text-muted font-medium">
                  {v.id.slice(0, 8)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            <tr className="border-b border-border-default/50">
              <td className="p-3 text-text-secondary">Candidates In / Out</td>
              {validations.map((v) => (
                <td key={v.id} className="p-3 text-center text-text-primary">
                  {formatNumber(v.candidatesOut, 0)}/{formatNumber(v.candidatesIn, 0)}
                </td>
              ))}
            </tr>
            <tr className="border-b border-border-default/50">
              <td className="p-3 text-text-secondary">Survival Rate</td>
              {validations.map((v) => (
                <td key={v.id} className="p-3 text-center text-text-primary">
                  {v.candidatesIn > 0 ? ((v.candidatesOut / v.candidatesIn) * 100).toFixed(1) : "0"}%
                </td>
              ))}
            </tr>
            <tr className="border-b border-border-default/50">
              <td className="p-3 text-text-secondary">Duration</td>
              {validations.map((v) => (
                <td key={v.id} className="p-3 text-center text-text-muted">
                  {formatDuration(v.durationMs)}
                </td>
              ))}
            </tr>
            <tr className="border-b border-border-default/50">
              <td className="p-3 text-text-secondary">Invocations</td>
              {validations.map((v) => (
                <td key={v.id} className="p-3 text-center text-text-muted">
                  {v.invocationCount}
                </td>
              ))}
            </tr>
            <tr className="border-b border-border-default/50">
              <td className="p-3 text-text-secondary">Rejections</td>
              {validations.map((v) => (
                <td key={v.id} className="p-3 text-center">
                  {v.rejections.length > 0 ? (
                    <span className="text-xs text-red-400">{v.rejections.join(", ")}</span>
                  ) : (
                    <span className="text-xs text-green-400">None</span>
                  )}
                </td>
              ))}
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}
