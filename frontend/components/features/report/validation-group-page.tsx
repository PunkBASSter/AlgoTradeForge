"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useValidationGroupDetail, useDeleteValidationGroup } from "@/hooks/use-validations";

interface ValidationGroupPageProps {
  groupId: string;
}

type Tab = "per-dss" | "cross-dss";

export function ValidationGroupPage({ groupId }: ValidationGroupPageProps) {
  const [activeTab, setActiveTab] = useState<Tab>("per-dss");
  const { data: group, isLoading } = useValidationGroupDetail(groupId);
  const deleteGroup = useDeleteValidationGroup();
  const router = useRouter();

  if (isLoading) {
    return <div className="p-6 text-text-muted">Loading validation group...</div>;
  }

  if (!group) {
    return <div className="p-6 text-accent-red">Validation group not found.</div>;
  }

  return (
    <div className="space-y-6 p-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-text-primary">
            Validation Group
          </h1>
          <p className="mt-1 text-sm text-text-secondary">
            {group.strategyName} &middot; {group.thresholdProfileName} &middot;{" "}
            <StatusBadge status={group.status} />
          </p>
        </div>
        <div className="flex gap-2">
          <a
            href={`/report/optimization-group/${group.optimizationGroupId}`}
            className="rounded bg-bg-surface px-3 py-1.5 text-sm text-text-secondary hover:bg-bg-hover border border-border-default"
          >
            Source Optimization
          </a>
          <button
            onClick={() => {
              deleteGroup.mutate(groupId, {
                onSuccess: () => router.push("/"),
              });
            }}
            className="rounded bg-red-900/30 px-3 py-1.5 text-sm text-red-400 hover:bg-red-900/50 border border-red-700"
          >
            Delete
          </button>
        </div>
      </div>

      {/* Metadata grid */}
      <div className="grid grid-cols-2 gap-4 rounded-lg border border-border-default bg-bg-panel p-4 sm:grid-cols-4">
        <Stat label="Total Runs" value={group.totalRuns} />
        <Stat label="Profile" value={group.thresholdProfileName} />
        <Stat label="Started" value={new Date(group.startedAt).toLocaleString()} />
        <Stat
          label="Completed"
          value={group.completedAt ? new Date(group.completedAt).toLocaleString() : "\u2014"}
        />
      </div>

      {/* Tabs */}
      <div className="border-b border-border-default">
        <nav className="flex gap-6" aria-label="Tabs">
          <TabButton
            label="Per-DSS Runs"
            active={activeTab === "per-dss"}
            onClick={() => setActiveTab("per-dss")}
          />
          <TabButton
            label="Cross-DSS"
            active={activeTab === "cross-dss"}
            onClick={() => setActiveTab("cross-dss")}
          />
        </nav>
      </div>

      {/* Tab content */}
      {activeTab === "per-dss" && (
        <div className="space-y-2">
          {group.runs?.map((run) => {
            const verdictColor =
              run.verdict === "Green"
                ? "text-green-400"
                : run.verdict === "Yellow"
                  ? "text-yellow-400"
                  : "text-red-400";
            return (
              <a
                key={run.id}
                href={`/report/validation/${run.id}`}
                className="flex items-center justify-between rounded-lg border border-border-default bg-bg-surface p-4 hover:bg-bg-hover transition-colors"
              >
                <div className="text-sm">
                  <span className="text-text-primary">
                    {run.dss?.[0]?.assetName ?? "Unknown"} / {run.dss?.[0]?.exchange ?? "\u2014"} / {run.dss?.[0]?.timeFrame ?? "\u2014"}
                  </span>
                  <span className="ml-3 text-text-muted">{run.status}</span>
                </div>
                <div className="flex items-center gap-4 text-sm">
                  <span className="text-text-secondary">
                    {run.candidatesIn} &rarr; {run.candidatesOut}
                  </span>
                  <span className={verdictColor}>{run.verdict}</span>
                  <span className="text-text-muted">
                    Score: {run.compositeScore.toFixed(2)}
                  </span>
                </div>
              </a>
            );
          })}
        </div>
      )}

      {activeTab === "cross-dss" && (
        <div className="text-sm text-text-muted">
          Cross-DSS validation comparison table &mdash; coming soon.
        </div>
      )}
    </div>
  );
}

/** Status badge with color coding — consistent with optimization-group-page. */
function StatusBadge({ status }: { status: string }) {
  const colorClass = (() => {
    switch (status) {
      case "Completed": return "bg-green-900/30 text-green-400 border-green-700";
      case "InProgress": return "bg-blue-900/30 text-blue-400 border-blue-700";
      case "PartiallyCompleted": return "bg-yellow-900/30 text-yellow-400 border-yellow-700";
      case "Failed": return "bg-red-900/30 text-red-400 border-red-700";
      case "Cancelled": return "bg-yellow-900/30 text-yellow-400 border-yellow-700";
      default: return "bg-bg-surface text-text-muted border-border-default";
    }
  })();

  const label = status === "InProgress" ? "In Progress"
    : status === "PartiallyCompleted" ? "Partial"
    : status;

  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${colorClass}`}>
      {label}
    </span>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div>
      <div className="text-xs text-text-muted">{label}</div>
      <div className="mt-0.5 text-sm text-text-primary">{String(value)}</div>
    </div>
  );
}

function TabButton({
  label,
  active,
  onClick,
}: {
  label: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      className={`pb-3 text-sm font-medium border-b-2 transition-colors ${
        active
          ? "border-accent-blue text-text-primary"
          : "border-transparent text-text-muted hover:text-text-primary hover:border-border-default"
      }`}
    >
      {label}
    </button>
  );
}
