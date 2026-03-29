"use client";

import React from "react";
import { useRouter } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import {
  useValidationDetail,
  useValidationStatus,
  useValidationEquity,
  useDeleteValidation,
} from "@/hooks/use-validations";
import { CompositeScorecard } from "@/components/features/validation/composite-scorecard";
import { StagePipeline } from "@/components/features/validation/stage-pipeline";
import { VerdictBadge } from "@/components/features/validation/verdict-badge";
import { EquityComparisonChart } from "@/components/features/validation/equity-comparison-chart";
import { DrawdownChart } from "@/components/features/validation/drawdown-chart";
import { MonthlyReturnsHeatmap } from "@/components/features/validation/monthly-returns-heatmap";
import { StatItem } from "@/components/ui/stat-item";
import { Button } from "@/components/ui/button";
import { Tabs } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/skeleton";
import { formatDuration, formatNumber } from "@/lib/utils/format";
const TABS = [
  { id: "scorecard", label: "Scorecard" },
  { id: "stages", label: "Stages" },
  { id: "charts", label: "Charts" },
];

export default function ValidationReportPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = React.use(params);
  const router = useRouter();
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = React.useState("scorecard");

  const {
    data: validation,
    isLoading,
    error,
  } = useValidationDetail(id);

  const { data: statusData } = useValidationStatus(id);

  const isInProgress = validation?.status === "InProgress"
    || (statusData?.status === "InProgress" && !validation);

  const isCompleted = validation?.status === "Completed";
  const { data: equityData } = useValidationEquity(id, isCompleted && activeTab === "charts");

  const deleteMutation = useDeleteValidation();

  // When status polling detects completion, refetch the detail
  React.useEffect(() => {
    if (statusData?.status === "Completed") {
      queryClient.invalidateQueries({ queryKey: ["validation", id] });
    }
  }, [statusData?.status, id, queryClient]);

  const handleDelete = () => {
    if (!confirm("Delete this validation run? This cannot be undone.")) return;
    deleteMutation.mutate(id, {
      onSuccess: () => router.push("/"),
    });
  };

  if (error) {
    return (
      <div className="p-6 flex flex-col items-center justify-center gap-4">
        <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
          <h2 className="text-lg font-semibold text-accent-red mb-2">
            Failed to load validation
          </h2>
          <p className="text-sm text-text-secondary">{error.message}</p>
        </div>
      </div>
    );
  }

  if (isLoading || !validation) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <Skeleton variant="rect" height="80px" />
        <Skeleton variant="rect" height="400px" />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between">
        <div className="flex items-center gap-4">
          <div>
            <h1 className="text-xl font-bold text-text-primary">
              Validation: {validation.strategyName}
              {validation.strategyVersion && (
                <span className="ml-2 text-sm font-normal text-text-muted">
                  v{validation.strategyVersion}
                </span>
              )}
            </h1>
            <p className="text-sm text-text-secondary mt-1">
              Profile: {validation.thresholdProfileName}
            </p>
          </div>
          {!isInProgress && <VerdictBadge verdict={validation.verdict} size="lg" />}
        </div>
        {!isInProgress && (
          <div className="flex items-center gap-2">
            <Button
              variant="secondary"
              onClick={() => window.open(`/api/validations/${id}/report`, "_blank")}
            >
              Export HTML
            </Button>
            <Button
              variant="danger"
              onClick={handleDelete}
              loading={deleteMutation.isPending}
            >
              Delete
            </Button>
          </div>
        )}
      </div>

      {/* In-progress state */}
      {isInProgress && statusData && (
        <div className="rounded-lg border border-border-default bg-bg-panel p-4">
          <div className="flex items-center gap-3">
            <div className="h-2 w-2 rounded-full bg-accent-blue animate-pulse" />
            <span className="text-sm text-text-secondary">
              Validation in progress — Stage {statusData.currentStage}/{statusData.totalStages}
            </span>
          </div>
          <div className="mt-2 h-2 rounded-full bg-bg-tertiary overflow-hidden">
            <div
              className="h-full rounded-full bg-accent-blue transition-all"
              style={{ width: `${Math.round((statusData.currentStage / statusData.totalStages) * 100)}%` }}
            />
          </div>
        </div>
      )}

      {/* Error/cancelled banner */}
      {validation.errorMessage && !isInProgress && (
        <div className="p-4 rounded-lg border border-accent-red bg-red-900/10">
          <p className="text-sm font-medium text-accent-red mb-1">
            {validation.status === "Cancelled" ? "Cancelled" : "Error"}
          </p>
          <p className="text-sm text-text-secondary">{validation.errorMessage}</p>
        </div>
      )}

      {/* Run info */}
      {!isInProgress && (
        <div className="rounded-lg border border-border-default bg-bg-panel p-4">
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
            <StatItem label="Composite Score" value={`${validation.compositeScore.toFixed(1)}/100`} />
            <StatItem label="Candidates In" value={formatNumber(validation.candidatesIn, 0)} />
            <StatItem label="Candidates Out" value={formatNumber(validation.candidatesOut, 0)} />
            <StatItem label="Duration" value={formatDuration(validation.durationMs)} />
            <StatItem label="Profile" value={validation.thresholdProfileName} />
            <StatItem label="Invocations" value={formatNumber(validation.invocationCount, 0)} />
            <StatItem label="Started" value={new Date(validation.startedAt).toLocaleString()} />
            {validation.completedAt && (
              <StatItem label="Completed" value={new Date(validation.completedAt).toLocaleString()} />
            )}
          </div>
        </div>
      )}

      {/* Tabbed content */}
      {!isInProgress && validation.status === "Completed" && (
        <Tabs tabs={TABS} activeTab={activeTab} onTabChange={setActiveTab}>
          {activeTab === "scorecard" && (
            <CompositeScorecard validation={validation} />
          )}
          {activeTab === "stages" && (
            <StagePipeline stages={validation.stageResults} />
          )}
          {activeTab === "charts" && (
            <div className="space-y-6">
              {equityData && equityData.trials.length > 0 ? (
                <>
                  <EquityComparisonChart trials={equityData.trials} />
                  <DrawdownChart trial={equityData.trials[0]} />
                  <MonthlyReturnsHeatmap trial={equityData.trials[0]} />
                </>
              ) : (
                <div className="rounded-lg border border-border-default bg-bg-panel p-6 text-center">
                  <p className="text-sm text-text-muted">
                    {equityData ? "No survivor equity data available." : "Loading chart data..."}
                  </p>
                </div>
              )}
            </div>
          )}
        </Tabs>
      )}
    </div>
  );
}
