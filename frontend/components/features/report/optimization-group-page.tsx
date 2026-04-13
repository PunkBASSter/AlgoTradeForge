"use client";

// T055 - OptimizationGroupPage — tabbed layout for per-DSS optimization group

import React, { useState } from "react";
import { useRouter } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import {
  useOptimizationGroupDetail,
  useDeleteOptimizationGroup,
} from "@/hooks/use-optimization-groups";
import { useRunGroupValidation } from "@/hooks/use-validation-groups";
import { CrossDssTrialsTable } from "@/components/features/report/cross-dss-trials-table";
import { RunValidationDialog } from "@/components/features/validation/run-validation-dialog";
import { StatItem } from "@/components/ui/stat-item";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  formatDuration,
  formatNumber,
} from "@/lib/utils/format";
import type { DataSubscriptionInput } from "@/types/api";
import type { GroupRunDetail } from "@/types/optimization-group";

type TabId = "per-dss" | "cross-dss";

/** Status badge with color coding. */
function StatusBadge({ status }: { status: string }) {
  const colorClass = (() => {
    switch (status) {
      case "Completed": return "bg-green-900/30 text-green-400 border-green-700";
      case "InProgress": return "bg-blue-900/30 text-blue-400 border-blue-700";
      case "Failed": return "bg-red-900/30 text-red-400 border-red-700";
      case "Cancelled": return "bg-yellow-900/30 text-yellow-400 border-yellow-700";
      default: return "bg-bg-surface text-text-muted border-border-default";
    }
  })();

  const label = status === "InProgress" ? "In Progress" : status;

  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${colorClass}`}>
      {label}
    </span>
  );
}

/** Format a DSS array as a compact string. */
function formatDss(dss: DataSubscriptionInput[]): string {
  return dss
    .map((d) => `${d.assetName}/${d.exchange}${d.timeFrame ? `/${d.timeFrame}` : ""}`)
    .join(", ");
}

/** Per-DSS child run row. */
function ChildRunRow({ run }: { run: GroupRunDetail }) {
  const router = useRouter();

  return (
    <tr
      onClick={() => router.push(`/report/optimization/${run.id}`)}
      className="bg-bg-surface transition-colors hover:bg-bg-hover cursor-pointer"
    >
      <td className="px-4 py-3 text-text-primary text-sm">{run.id.substring(0, 8)}</td>
      <td className="px-4 py-3 text-text-secondary text-sm">{formatDss(run.dss)}</td>
      <td className="px-4 py-3"><StatusBadge status={run.status} /></td>
      <td className="px-4 py-3 text-text-primary text-sm">
        {formatNumber(run.totalCombinations, 0)}
      </td>
      <td className="px-4 py-3 text-text-primary text-sm">{run.keptTrials}</td>
      <td className="px-4 py-3 text-text-primary text-sm">{run.filteredTrials}</td>
      <td className="px-4 py-3 text-text-primary text-sm">{run.failedTrials}</td>
      <td className="px-4 py-3 text-text-secondary text-sm">
        {run.durationMs > 0 ? formatDuration(run.durationMs) : "\u2014"}
      </td>
    </tr>
  );
}

/** Per-DSS runs tab content. */
function PerDssRunsTab({ runs }: { runs: GroupRunDetail[] }) {
  return (
    <div className="overflow-x-auto rounded-md border border-border-default" data-testid="per-dss-runs-table">
      <table className="w-full text-left text-sm">
        <thead>
          <tr className="border-b border-border-default bg-bg-panel">
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Run ID</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">DSS</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Status</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Combinations</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Kept</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Filtered</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Failed</th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">Duration</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {runs.length === 0 ? (
            <tr>
              <td colSpan={8} className="px-4 py-8 text-center text-text-muted">
                No per-DSS runs found
              </td>
            </tr>
          ) : (
            runs.map((run) => <ChildRunRow key={run.id} run={run} />)
          )}
        </tbody>
      </table>
    </div>
  );
}

interface OptimizationGroupPageProps {
  groupId: string;
}

export function OptimizationGroupPage({ groupId }: OptimizationGroupPageProps) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<TabId>("per-dss");
  const [validationDialogOpen, setValidationDialogOpen] = useState(false);

  const {
    data: group,
    isLoading,
    error,
  } = useOptimizationGroupDetail(groupId);

  const deleteMutation = useDeleteOptimizationGroup();
  const runGroupValidation = useRunGroupValidation();

  const isCompleted = group?.status === "Completed";
  const isInProgress = group?.status === "InProgress";

  const handleRunValidation = (profileName: string) => {
    runGroupValidation.mutate(
      { optimizationGroupId: groupId, thresholdProfileName: profileName },
      {
        onSuccess: (data) => {
          setValidationDialogOpen(false);
          router.push(`/report/validation/${data.id}`);
        },
      },
    );
  };

  const handleDelete = () => {
    if (!confirm("Delete this optimization group and all its runs? This cannot be undone.")) return;
    deleteMutation.mutate(groupId, {
      onSuccess: () => router.push("/"),
    });
  };

  if (error) {
    return (
      <div className="p-6 flex flex-col items-center justify-center gap-4">
        <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
          <h2 className="text-lg font-semibold text-accent-red mb-2">
            Failed to load optimization group
          </h2>
          <p className="text-sm text-text-secondary">
            {error.message}
          </p>
        </div>
      </div>
    );
  }

  if (isLoading || !group) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <Skeleton variant="rect" height="80px" />
        <Skeleton variant="rect" height="400px" />
      </div>
    );
  }

  const methodLabel = group.optimizationMethod === "Genetic" ? "Genetic" : "Grid";

  return (
    <div className="p-6 space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-bold text-text-primary">
            Optimization Group: {group.strategyName}
            <span className="ml-2 text-sm font-normal text-text-muted">
              v{group.strategyVersion}
            </span>
          </h1>
          <p className="text-sm text-text-secondary mt-1">
            {methodLabel} -- {group.totalRuns} DSS runs
          </p>
        </div>
        {!isInProgress && (
          <div className="flex items-center gap-2">
            {group.inputJson && (
              <Button
                variant="secondary"
                onClick={() => {
                  sessionStorage.setItem("rerun-optimization-config", group.inputJson!);
                  router.push(`/${group.strategyName}/optimization`);
                }}
              >
                Re-run
              </Button>
            )}
            {isCompleted && (
              <Button
                variant="primary"
                onClick={() => setValidationDialogOpen(true)}
              >
                Run Validation
              </Button>
            )}
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

      {/* Group metadata */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
          <StatItem label="Strategy" value={group.strategyName} />
          <StatItem label="Method" value={methodLabel} />
          <StatItem
            label="Status"
            value={<StatusBadge status={group.status} />}
          />
          <StatItem
            label="Started"
            value={new Date(group.startedAt).toLocaleString()}
          />
          {group.completedAt && (
            <StatItem
              label="Completed"
              value={new Date(group.completedAt).toLocaleString()}
            />
          )}
          <StatItem
            label="Total Runs"
            value={formatNumber(group.totalRuns, 0)}
          />
          <StatItem
            label="Completed Runs"
            value={`${group.completedRuns}/${group.totalRuns}`}
          />
          {group.failedRuns > 0 && (
            <StatItem
              label="Failed Runs"
              value={formatNumber(group.failedRuns, 0)}
            />
          )}
        </div>
      </div>

      {/* Tabs */}
      <div className="border-b border-border-default">
        <nav className="flex gap-6" aria-label="Tabs">
          <button
            onClick={() => setActiveTab("per-dss")}
            className={`pb-3 text-sm font-medium border-b-2 transition-colors ${
              activeTab === "per-dss"
                ? "border-accent-blue text-text-primary"
                : "border-transparent text-text-muted hover:text-text-primary hover:border-border-default"
            }`}
          >
            Per-DSS Runs
          </button>
          <button
            onClick={() => setActiveTab("cross-dss")}
            className={`pb-3 text-sm font-medium border-b-2 transition-colors ${
              activeTab === "cross-dss"
                ? "border-accent-blue text-text-primary"
                : "border-transparent text-text-muted hover:text-text-primary hover:border-border-default"
            }`}
          >
            Cross-DSS
          </button>
        </nav>
      </div>

      {/* Tab content */}
      {activeTab === "per-dss" && <PerDssRunsTab runs={group.runs} />}
      {activeTab === "cross-dss" && <CrossDssTrialsTable groupId={groupId} />}

      {/* Run Validation dialog */}
      <RunValidationDialog
        open={validationDialogOpen}
        onClose={() => setValidationDialogOpen(false)}
        onSubmit={handleRunValidation}
        loading={runGroupValidation.isPending}
      />
    </div>
  );
}
