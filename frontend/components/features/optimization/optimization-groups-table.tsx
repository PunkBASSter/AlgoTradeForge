"use client";

// T041 - OptimizationGroupsTable component with expandable group rows

import { useState, useMemo, useCallback } from "react";
import { useRouter } from "next/navigation";
import { formatDuration } from "@/lib/utils/format";
import type { OptimizationGroupSummary } from "@/types/optimization-group";
import type { DataSubscriptionInput } from "@/types/api";

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

/** Chevron icon for expand/collapse. */
function ChevronIcon({ expanded }: { expanded: boolean }) {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={`transition-transform ${expanded ? "rotate-90" : ""}`}
    >
      <path d="M6 4l4 4-4 4" />
    </svg>
  );
}

/** Format a DSS array as a compact string. */
function formatDss(dss: DataSubscriptionInput[]): string {
  return dss
    .map((d) => `${d.assetName}/${d.exchange}${d.timeFrame ? `/${d.timeFrame}` : ""}`)
    .join(", ");
}

interface ChildRun {
  id: string;
  dss: DataSubscriptionInput[];
  status: string;
  trialCount: number;
  durationMs: number;
}

interface OptimizationGroupsTableProps {
  groups: OptimizationGroupSummary[];
  childRuns?: Record<string, ChildRun[]>;
  isLoading?: boolean;
}

export function OptimizationGroupsTable({
  groups,
  childRuns,
  isLoading,
}: OptimizationGroupsTableProps) {
  const router = useRouter();
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());

  const toggleExpand = useCallback((groupId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(groupId)) {
        next.delete(groupId);
      } else {
        next.add(groupId);
      }
      return next;
    });
  }, []);

  const rows = useMemo(() => {
    const result: React.ReactNode[] = [];

    for (const group of groups) {
      const isExpanded = expandedGroups.has(group.id);
      const dssCount = group.subscriptions.length;
      const methodLabel = group.optimizationMethod === "Genetic" ? "Genetic" : "Grid";

      result.push(
        <tr
          key={group.id}
          onClick={() => router.push(`/report/optimization-group/${group.id}`)}
          className="bg-bg-surface transition-colors hover:bg-bg-hover cursor-pointer"
        >
          <td className="px-4 py-3 text-text-primary">
            <button
              onClick={(e) => toggleExpand(group.id, e)}
              className="p-0.5 rounded hover:bg-bg-hover text-text-muted hover:text-text-primary transition-colors"
              aria-label={isExpanded ? "Collapse" : "Expand"}
            >
              <ChevronIcon expanded={isExpanded} />
            </button>
          </td>
          <td className="px-4 py-3 text-text-primary font-medium">
            {group.strategyName}
            <span className="ml-1.5 text-xs font-normal text-text-muted">
              v{group.strategyVersion}
            </span>
          </td>
          <td className="px-4 py-3 text-text-primary">{group.id.substring(0, 8)}</td>
          <td className="px-4 py-3 text-text-primary">{methodLabel}</td>
          <td className="px-4 py-3"><StatusBadge status={group.status} /></td>
          <td className="px-4 py-3 text-text-primary">{dssCount}</td>
          <td className="px-4 py-3 text-text-primary">
            {group.completedRuns}/{group.totalRuns}
          </td>
          <td className="px-4 py-3 text-text-secondary text-xs">
            {new Date(group.startedAt).toLocaleString()}
          </td>
        </tr>,
      );

      // Expanded child rows
      if (isExpanded) {
        const children = childRuns?.[group.id];
        if (children && children.length > 0) {
          for (const child of children) {
            result.push(
              <tr
                key={`${group.id}-${child.id}`}
                onClick={() => router.push(`/report/optimization/${child.id}`)}
                className="bg-bg-panel/50 transition-colors hover:bg-bg-hover cursor-pointer"
              >
                <td className="px-4 py-2 text-text-muted" />
                <td className="px-4 py-2 text-text-secondary text-xs pl-10" colSpan={2}>
                  {formatDss(child.dss)}
                </td>
                <td className="px-4 py-2 text-text-secondary text-xs">{child.id.substring(0, 8)}</td>
                <td className="px-4 py-2"><StatusBadge status={child.status} /></td>
                <td className="px-4 py-2 text-text-secondary text-xs">{child.trialCount} trials</td>
                <td className="px-4 py-2 text-text-secondary text-xs">
                  {child.durationMs > 0 ? formatDuration(child.durationMs) : "\u2014"}
                </td>
                <td className="px-4 py-2" />
              </tr>,
            );
          }
        } else {
          result.push(
            <tr key={`${group.id}-empty`}>
              <td colSpan={8} className="px-4 py-2 pl-10 text-text-muted text-xs italic">
                No child runs loaded
              </td>
            </tr>,
          );
        }
      }
    }

    return result;
  }, [groups, expandedGroups, childRuns, router, toggleExpand]);

  if (isLoading) {
    return (
      <div className="p-8 text-center text-text-muted text-sm">
        Loading optimization groups...
      </div>
    );
  }

  return (
    <div className="overflow-x-auto rounded-md border border-border-default" data-testid="optimization-groups-table">
      <table className="w-full text-left text-sm">
        <thead>
          <tr className="border-b border-border-default bg-bg-panel">
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted w-10" />
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Strategy
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Group ID
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Method
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Status
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              DSS Count
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Runs
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Started
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {groups.length === 0 ? (
            <tr>
              <td colSpan={8} className="px-4 py-8 text-center text-text-muted">
                No optimization groups found
              </td>
            </tr>
          ) : (
            rows
          )}
        </tbody>
      </table>
    </div>
  );
}
