"use client";

import React from "react";
import type { CandidateVerdict } from "@/types/validation";

function formatMetricValue(value: number): string {
  if (Number.isInteger(value) && Math.abs(value) < 1_000_000) return value.toFixed(0);
  if (Math.abs(value) < 0.01) return value.toExponential(2);
  return value.toFixed(4);
}

interface CandidateVerdictsTableProps {
  verdicts: CandidateVerdict[];
}

export function CandidateVerdictsTable({ verdicts }: CandidateVerdictsTableProps) {
  const [sortKey, setSortKey] = React.useState<string | null>(null);
  const [sortAsc, setSortAsc] = React.useState(true);

  if (verdicts.length === 0) return null;

  // Gather all unique metric keys from all verdicts
  const metricKeys = React.useMemo(() => {
    const keys = new Set<string>();
    for (const v of verdicts) {
      for (const k of Object.keys(v.metrics)) keys.add(k);
    }
    return [...keys];
  }, [verdicts]);

  const handleSort = (key: string) => {
    if (sortKey === key) {
      setSortAsc((v) => !v);
    } else {
      setSortKey(key);
      setSortAsc(true);
    }
  };

  const sorted = React.useMemo(() => {
    if (!sortKey) return verdicts;
    return [...verdicts].sort((a, b) => {
      const va = a.metrics[sortKey] ?? 0;
      const vb = b.metrics[sortKey] ?? 0;
      return sortAsc ? va - vb : vb - va;
    });
  }, [verdicts, sortKey, sortAsc]);

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead>
          <tr className="border-b border-border-default">
            <th className="text-left p-1.5 text-text-muted font-medium">Trial</th>
            <th className="text-center p-1.5 text-text-muted font-medium">Pass</th>
            <th className="text-left p-1.5 text-text-muted font-medium">Reason</th>
            {metricKeys.map((key) => (
              <th
                key={key}
                className="text-right p-1.5 text-text-muted font-medium cursor-pointer hover:text-text-primary"
                onClick={() => handleSort(key)}
              >
                {key}
                {sortKey === key && (sortAsc ? " \u25B2" : " \u25BC")}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sorted.map((v) => (
            <tr key={v.trialId} className="border-b border-border-default/50 hover:bg-bg-tertiary/30">
              <td className="p-1.5 text-text-secondary font-mono">
                {v.trialId.slice(0, 8)}
              </td>
              <td className="p-1.5 text-center">
                {v.passed ? (
                  <span className="text-green-400">&#x2713;</span>
                ) : (
                  <span className="text-red-400">&#x2717;</span>
                )}
              </td>
              <td className="p-1.5 text-text-muted">
                {v.reasonCode ?? "\u2014"}
              </td>
              {metricKeys.map((key) => (
                <td key={key} className="p-1.5 text-right text-text-secondary font-mono">
                  {v.metrics[key] !== undefined
                    ? formatMetricValue(v.metrics[key])
                    : "\u2014"}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
