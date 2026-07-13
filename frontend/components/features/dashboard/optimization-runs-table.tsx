"use client";

import { useState, useMemo, useCallback } from "react";
import { useRouter } from "next/navigation";
import { ChevronIcon } from "@/components/ui/chevron-icon";
import { StatusBadge } from "@/components/ui/status-badge";
import { DssCell } from "./dss-cell";
import { groupOptimizationRuns } from "@/lib/utils/group-optimization-runs";
import { formatDuration } from "@/lib/utils/format";
import type { OptimizationRun } from "@/types/api";

interface OptimizationRunsTableProps {
  data: OptimizationRun[];
}

/** Determine the worst status across a set of runs for aggregate display. */
function worstStatus(runs: OptimizationRun[]): string {
  const priority = ["Failed", "InProgress", "Enqueued", "Cancelled", "Completed"];
  for (const s of priority) {
    if (runs.some((r) => r.status === s)) return s;
  }
  return "Completed";
}

const COL_COUNT = 7;

export function OptimizationRunsTable({ data }: OptimizationRunsTableProps) {
  const router = useRouter();
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());

  const groupedRows = useMemo(() => groupOptimizationRuns(data), [data]);

  const toggleExpand = useCallback((groupId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(groupId)) next.delete(groupId);
      else next.add(groupId);
      return next;
    });
  }, []);

  const rows = useMemo(() => {
    const result: React.ReactNode[] = [];

    for (const entry of groupedRows) {
      if (entry.type === "standalone") {
        const run = entry.run;
        result.push(
          <tr
            key={run.id}
            onClick={() => router.push(`/report/optimization/${run.id}`)}
            className="bg-bg-surface transition-colors hover:bg-bg-hover cursor-pointer"
          >
            <td className="px-4 py-3 w-10" />
            <td className="px-4 py-3 text-text-primary">{run.strategyVersion}</td>
            <td className="px-4 py-3 text-text-primary">{run.id.substring(0, 8)}</td>
            <td className="px-4 py-3">
              {run.dataSubscriptions[0] && <DssCell subscription={run.dataSubscriptions[0]} />}
            </td>
            <td className="px-4 py-3 text-text-primary">{run.totalCombinations}</td>
            <td className="px-4 py-3"><StatusBadge status={run.status} /></td>
            <td className="px-4 py-3 text-text-primary">
              {run.status === "InProgress" || run.status === "Enqueued"
                ? "\u2014"
                : formatDuration(run.durationMs)}
            </td>
          </tr>,
        );
        continue;
      }

      // Group row
      const { groupId, runs } = entry;
      const isExpanded = expandedGroups.has(groupId);
      const first = runs[0];
      const totalCombinations = runs.reduce((sum, r) => sum + r.totalCombinations, 0);
      const completedCount = runs.filter((r) => r.status === "Completed").length;
      const anyInProgress = runs.some((r) => r.status === "InProgress" || r.status === "Enqueued");
      const totalDurationMs = runs.reduce((sum, r) => sum + r.durationMs, 0);
      const aggregateStatus = worstStatus(runs);

      result.push(
        <tr
          key={groupId}
          onClick={() => router.push(`/report/optimization-group/${groupId}`)}
          className="bg-bg-surface transition-colors hover:bg-bg-hover cursor-pointer"
        >
          <td className="px-4 py-3 w-10 text-text-primary">
            <button
              onClick={(e) => toggleExpand(groupId, e)}
              className="p-0.5 rounded hover:bg-bg-hover text-text-muted hover:text-text-primary transition-colors"
              aria-label={isExpanded ? "Collapse" : "Expand"}
            >
              <ChevronIcon expanded={isExpanded} />
            </button>
          </td>
          <td className="px-4 py-3 text-text-primary">{first.strategyVersion}</td>
          <td className="px-4 py-3 text-accent-blue font-medium">
            {groupId.substring(0, 8)}
          </td>
          <td className="px-4 py-3 text-text-secondary text-xs">
            {runs.length} subscription{runs.length !== 1 ? "s" : ""}
          </td>
          <td className="px-4 py-3 text-text-primary">{totalCombinations}</td>
          <td className="px-4 py-3">
            <span className="flex items-center gap-2">
              <StatusBadge status={aggregateStatus} />
              <span className="text-text-muted text-xs">{completedCount}/{runs.length}</span>
            </span>
          </td>
          <td className="px-4 py-3 text-text-primary">
            {anyInProgress ? "\u2014" : formatDuration(totalDurationMs)}
          </td>
        </tr>,
      );

      // Expanded child rows
      if (isExpanded) {
        for (const run of runs) {
          result.push(
            <tr
              key={`${groupId}-${run.id}`}
              onClick={(e) => {
                e.stopPropagation();
                router.push(`/report/optimization/${run.id}`);
              }}
              className="bg-bg-panel/50 transition-colors hover:bg-bg-hover cursor-pointer"
            >
              <td className="px-4 py-2" />
              <td className="px-4 py-2" />
              <td className="px-4 py-2 text-text-secondary text-xs">
                {run.id.substring(0, 8)}
              </td>
              <td className="px-4 py-2">
                {run.dataSubscriptions[0] && <DssCell subscription={run.dataSubscriptions[0]} />}
              </td>
              <td className="px-4 py-2 text-text-secondary text-xs">{run.totalCombinations}</td>
              <td className="px-4 py-2"><StatusBadge status={run.status} /></td>
              <td className="px-4 py-2 text-text-secondary text-xs">
                {run.status === "InProgress" || run.status === "Enqueued"
                  ? "\u2014"
                  : formatDuration(run.durationMs)}
              </td>
            </tr>,
          );
        }
      }
    }

    return result;
  }, [groupedRows, expandedGroups, router, toggleExpand]);

  return (
    <div className="overflow-x-auto rounded-md border border-border-default" data-testid="runs-table">
      <table className="w-full text-left text-sm">
        <thead>
          <tr className="border-b border-border-default bg-bg-panel">
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted w-10" />
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Version
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Run / Group
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              DSS
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Combinations
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Status
            </th>
            <th className="px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted">
              Duration
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {data.length === 0 ? (
            <tr>
              <td colSpan={COL_COUNT} className="px-4 py-8 text-center text-text-muted">
                No optimization runs found
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
