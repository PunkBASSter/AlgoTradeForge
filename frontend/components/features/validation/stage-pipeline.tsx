"use client";

import React from "react";
import { CandidateVerdictsTable } from "./candidate-verdicts-table";
import type { StageResultResponse, CandidateVerdict } from "@/types/validation";
import { formatDuration } from "@/lib/utils/format";

const STAGE_NAMES: Record<string, string> = {
  PreFlight: "Pre-flight Checks",
  BasicProfitability: "Basic Profitability",
  StatisticalSignificance: "Statistical Significance",
  ParameterLandscape: "Parameter Landscape",
  WalkForwardOptimization: "Walk-Forward Optimization",
  WalkForwardMatrix: "Walk-Forward Matrix",
  MonteCarloPnlDeltasPermutation: "Monte Carlo & Permutation",
  SelectionBiasAudit: "Selection Bias Audit",
};

function survivalColor(rate: number): string {
  if (rate > 0.8) return "bg-green-500";
  if (rate > 0.5) return "bg-yellow-500";
  return "bg-red-500";
}

function StageRow({ stage }: { stage: StageResultResponse }) {
  const [expanded, setExpanded] = React.useState(false);
  const survivalRate = stage.candidatesIn > 0
    ? stage.candidatesOut / stage.candidatesIn
    : 0;
  const eliminated = stage.candidatesIn - stage.candidatesOut;

  let verdicts: CandidateVerdict[] = [];
  if (stage.detailJson) {
    try {
      verdicts = JSON.parse(stage.detailJson) as CandidateVerdict[];
    } catch {
      // malformed JSON
    }
  }

  return (
    <div className="border border-border-default rounded-lg bg-bg-panel overflow-hidden">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="w-full flex items-center gap-3 p-3 text-left hover:bg-bg-tertiary/50 transition-colors"
      >
        {/* Stage number */}
        <span className="flex-none w-6 h-6 rounded-full bg-bg-tertiary flex items-center justify-center text-xs font-bold text-text-muted">
          {stage.stageNumber}
        </span>

        {/* Name */}
        <span className="flex-1 text-sm font-medium text-text-primary">
          {STAGE_NAMES[stage.stageName] ?? stage.stageName}
        </span>

        {/* Survival bar */}
        <div className="flex-none w-32">
          <div className="h-2 rounded-full bg-bg-tertiary overflow-hidden">
            <div
              className={`h-full rounded-full ${survivalColor(survivalRate)} transition-all`}
              style={{ width: `${Math.round(survivalRate * 100)}%` }}
            />
          </div>
        </div>

        {/* Stats */}
        <span className="flex-none text-xs text-text-muted w-20 text-right">
          {stage.candidatesIn}/{stage.candidatesOut}
          {eliminated > 0 && (
            <span className="text-red-400 ml-1">(-{eliminated})</span>
          )}
        </span>

        {/* Duration */}
        <span className="flex-none text-xs text-text-muted w-16 text-right">
          {formatDuration(stage.durationMs)}
        </span>

        {/* Expand indicator */}
        <span className="flex-none text-text-muted text-xs w-4">
          {expanded ? "\u25B2" : "\u25BC"}
        </span>
      </button>

      {expanded && verdicts.length > 0 && (
        <div className="border-t border-border-default p-3">
          <CandidateVerdictsTable verdicts={verdicts} />
        </div>
      )}
    </div>
  );
}

interface StagePipelineProps {
  stages: StageResultResponse[];
}

export function StagePipeline({ stages }: StagePipelineProps) {
  return (
    <div className="space-y-2">
      {stages.map((stage) => (
        <StageRow key={stage.stageNumber} stage={stage} />
      ))}
    </div>
  );
}
