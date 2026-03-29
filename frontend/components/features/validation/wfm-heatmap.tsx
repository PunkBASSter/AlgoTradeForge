"use client";

import { Fragment, useMemo } from "react";
import type { WfmResult } from "@/types/validation";

interface WfmHeatmapProps {
  result: WfmResult;
}

function wfeColor(wfe: number, passed: boolean): string {
  if (!passed) return "bg-red-500/60";
  if (wfe >= 0.8) return "bg-green-500/80";
  if (wfe >= 0.6) return "bg-green-400/70";
  if (wfe >= 0.5) return "bg-green-300/60";
  return "bg-yellow-400/60";
}

function isInCluster(
  row: number,
  col: number,
  cluster: WfmResult["largestContiguousCluster"],
): boolean {
  if (!cluster) return false;
  return (
    row >= cluster.row &&
    row < cluster.row + cluster.rows &&
    col >= cluster.col &&
    col < cluster.col + cluster.cols
  );
}

export function WfmHeatmap({ result }: WfmHeatmapProps) {
  const { grid, periodCounts, oosPcts, largestContiguousCluster } = result;

  const rows = useMemo(
    () =>
      periodCounts.map((period, pi) => ({
        period,
        cells: oosPcts.map((oos, oi) => {
          const cell = grid[pi]?.[oi];
          return {
            oos,
            wfe: cell?.walkForwardEfficiency ?? 0,
            passed: cell?.passed ?? false,
            inCluster: isInCluster(pi, oi, largestContiguousCluster),
          };
        }),
      })),
    [grid, periodCounts, oosPcts, largestContiguousCluster],
  );

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium text-zinc-300">
        Walk-Forward Matrix
      </h3>

      {/* Column headers — OOS percentages */}
      <div className="grid gap-1" style={{ gridTemplateColumns: `80px repeat(${oosPcts.length}, 1fr)` }}>
        <div className="text-xs text-zinc-500" />
        {oosPcts.map((oos) => (
          <div key={oos} className="text-center text-xs text-zinc-400">
            {(oos * 100).toFixed(0)}% OOS
          </div>
        ))}

        {/* Grid rows */}
        {rows.map(({ period, cells }) => (
          <Fragment key={`row-${period}`}>
            <div className="text-xs text-zinc-400 flex items-center">
              {period} periods
            </div>
            {cells.map(({ oos, wfe, passed, inCluster }) => (
              <div
                key={`${period}-${oos}`}
                className={`
                  relative rounded px-2 py-3 text-center text-xs font-mono
                  ${wfeColor(wfe, passed)}
                  ${inCluster ? "ring-2 ring-blue-400" : ""}
                  transition-all hover:brightness-110
                `}
                title={`WFE: ${wfe.toFixed(3)} | ${passed ? "PASS" : "FAIL"}`}
              >
                {wfe.toFixed(2)}
              </div>
            ))}
          </Fr agment>
        ))}
      </div>

      {/* Legend */}
      <div className="flex items-center gap-4 text-xs text-zinc-500 mt-2">
        <span className="flex items-center gap-1">
          <span className="inline-block w-3 h-3 rounded bg-green-500/80" /> Pass
        </span>
        <span className="flex items-center gap-1">
          <span className="inline-block w-3 h-3 rounded bg-red-500/60" /> Fail
        </span>
        <span className="flex items-center gap-1">
          <span className="inline-block w-3 h-3 rounded ring-2 ring-blue-400" /> Cluster
        </span>
        {result.optimalReoptPeriod && (
          <span className="text-zinc-400">
            Optimal reopt: {result.optimalReoptPeriod} periods
          </span>
        )}
      </div>
    </div>
  );
}
