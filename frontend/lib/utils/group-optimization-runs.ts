import type { OptimizationRun } from "@/types/api";

export type GroupedRow =
  | { type: "standalone"; run: OptimizationRun }
  | { type: "group"; groupId: string; runs: OptimizationRun[] };

/**
 * Groups optimization runs by groupId while preserving API sort order.
 * Runs sharing a groupId are collected into a single entry at the position
 * of the first occurrence. Runs without a groupId become standalone entries.
 */
export function groupOptimizationRuns(runs: OptimizationRun[]): GroupedRow[] {
  const result: GroupedRow[] = [];
  const groupMap = new Map<string, OptimizationRun[]>();

  for (const run of runs) {
    if (!run.groupId) {
      result.push({ type: "standalone", run });
      continue;
    }

    const existing = groupMap.get(run.groupId);
    if (existing) {
      existing.push(run);
    } else {
      const groupRuns = [run];
      groupMap.set(run.groupId, groupRuns);
      result.push({ type: "group", groupId: run.groupId, runs: groupRuns });
    }
  }

  return result;
}
