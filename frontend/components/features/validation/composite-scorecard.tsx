"use client";

import { VerdictBadge } from "./verdict-badge";
import type { ValidationRun } from "@/types/validation";

const CATEGORY_LABELS: Record<string, string> = {
  Data: "Data Sufficiency",
  Stats: "Statistical Significance",
  Params: "Parameter Landscape",
  WFO: "Walk-Forward Optimization",
  WFM: "Walk-Forward Matrix",
  MC: "Monte Carlo & Permutation",
  SubPeriod: "Sub-Period Consistency",
};

const CATEGORY_ORDER = ["Data", "Stats", "Params", "WFO", "WFM", "MC", "SubPeriod"];

function scoreColor(score: number): string {
  if (score >= 70) return "bg-green-500";
  if (score >= 40) return "bg-yellow-500";
  return "bg-red-500";
}

function ScoreGauge({ score, verdict }: { score: number; verdict: string }) {
  const borderColor = verdict === "Green"
    ? "border-green-500/40"
    : verdict === "Yellow"
      ? "border-yellow-500/40"
      : "border-red-500/40";
  const textColor = verdict === "Green"
    ? "text-green-400"
    : verdict === "Yellow"
      ? "text-yellow-400"
      : "text-red-400";

  return (
    <div className={`flex flex-col items-center justify-center w-32 h-32 rounded-full border-4 ${borderColor}`}>
      <span className={`text-3xl font-bold ${textColor}`}>{Math.round(score)}</span>
      <span className="text-xs text-text-muted">/100</span>
    </div>
  );
}

function CategoryBar({ name, score }: { name: string; score: number }) {
  return (
    <div className="space-y-1">
      <div className="flex justify-between text-sm">
        <span className="text-text-secondary">{CATEGORY_LABELS[name] ?? name}</span>
        <span className="text-text-muted font-mono">{score.toFixed(0)}</span>
      </div>
      <div className="h-2 rounded-full bg-bg-tertiary overflow-hidden">
        <div
          className={`h-full rounded-full ${scoreColor(score)} transition-all`}
          style={{ width: `${Math.min(100, Math.max(0, score))}%` }}
        />
      </div>
    </div>
  );
}

function MetaOverfittingWarning({ count }: { count: number }) {
  if (count < 3) return null;

  const severity = count >= 10 ? "red" : count >= 5 ? "orange" : "yellow";
  const bgClass = severity === "red"
    ? "border-red-500 bg-red-900/10 text-red-400"
    : severity === "orange"
      ? "border-orange-500 bg-orange-900/10 text-orange-400"
      : "border-yellow-500 bg-yellow-900/10 text-yellow-400";

  return (
    <div className={`p-3 rounded-lg border ${bgClass}`}>
      <p className="text-sm font-medium">
        Meta-overfitting warning: This optimization has been validated {count} time{count !== 1 && "s"}.
      </p>
      <p className="text-xs mt-1 opacity-80">
        {count >= 10
          ? "Repeated validation severely compromises statistical integrity. Results should not be trusted."
          : count >= 5
            ? "Multiple re-validations may bias results. Consider this when interpreting the verdict."
            : "Repeated validation can itself constitute overfitting at a higher level of abstraction."}
      </p>
    </div>
  );
}

interface CompositeScorecardProps {
  validation: ValidationRun;
}

export function CompositeScorecard({ validation }: CompositeScorecardProps) {
  const { compositeScore, verdict, verdictSummary, rejections, categoryScores, invocationCount } = validation;

  return (
    <div className="space-y-6">
      {/* Score + verdict header */}
      <div className="flex items-center gap-6">
        <ScoreGauge score={compositeScore} verdict={verdict} />
        <div className="space-y-2">
          <VerdictBadge verdict={verdict} size="lg" />
          {verdictSummary && (
            <p className="text-sm text-text-secondary max-w-lg">{verdictSummary}</p>
          )}
        </div>
      </div>

      {/* Hard rejection alerts */}
      {rejections.length > 0 && (
        <div className="space-y-2">
          {rejections.map((r) => (
            <div key={r} className="p-3 rounded-lg border border-red-500 bg-red-900/10">
              <span className="text-sm font-semibold text-red-400">Hard Rejection: </span>
              <span className="text-sm text-text-secondary">{r}</span>
            </div>
          ))}
        </div>
      )}

      {/* Meta-overfitting warning */}
      <MetaOverfittingWarning count={invocationCount} />

      {/* Category sub-scores */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4 space-y-3">
        <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
          Category Scores
        </h3>
        {CATEGORY_ORDER.map((key) => (
          <CategoryBar key={key} name={key} score={categoryScores[key] ?? 0} />
        ))}
      </div>
    </div>
  );
}
