"use client";

import React from "react";
import { useRouter } from "next/navigation";
import { VerdictBadge } from "./verdict-badge";
import { Button } from "@/components/ui/button";
import { formatDuration, formatNumber } from "@/lib/utils/format";
import type { ValidationRunSummary } from "@/types/validation";

interface ValidationListTableProps {
  validations: ValidationRunSummary[];
  isLoading?: boolean;
}

export function ValidationListTable({ validations, isLoading }: ValidationListTableProps) {
  const router = useRouter();
  const [selected, setSelected] = React.useState<Set<string>>(new Set());

  const toggleSelect = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else if (next.size < 4) next.add(id); // max 4 for comparison
      return next;
    });
  };

  const handleCompare = () => {
    if (selected.size < 2) return;
    const ids = [...selected].join(",");
    router.push(`/report/validation/compare?ids=${ids}`);
  };

  if (isLoading) {
    return (
      <div className="rounded-lg border border-border-default bg-bg-panel p-6 text-center">
        <p className="text-sm text-text-muted">Loading validations...</p>
      </div>
    );
  }

  if (validations.length === 0) {
    return (
      <div className="rounded-lg border border-border-default bg-bg-panel p-6 text-center">
        <p className="text-sm text-text-muted">No validation runs found.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {selected.size >= 2 && (
        <div className="flex items-center gap-3">
          <Button variant="primary" onClick={handleCompare}>
            Compare Selected ({selected.size})
          </Button>
          <button
            type="button"
            className="text-xs text-text-muted hover:text-text-secondary"
            onClick={() => setSelected(new Set())}
          >
            Clear selection
          </button>
        </div>
      )}

      <div className="overflow-x-auto rounded-lg border border-border-default">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-bg-tertiary/50 border-b border-border-default">
              <th className="p-2 text-left w-8" />
              <th className="p-2 text-left text-text-muted font-medium">ID</th>
              <th className="p-2 text-left text-text-muted font-medium">Strategy</th>
              <th className="p-2 text-left text-text-muted font-medium">Profile</th>
              <th className="p-2 text-right text-text-muted font-medium">Score</th>
              <th className="p-2 text-center text-text-muted font-medium">Verdict</th>
              <th className="p-2 text-right text-text-muted font-medium">In/Out</th>
              <th className="p-2 text-right text-text-muted font-medium">Duration</th>
              <th className="p-2 text-left text-text-muted font-medium">Started</th>
            </tr>
          </thead>
          <tbody>
            {validations.map((v) => (
              <tr
                key={v.id}
                className="border-b border-border-default/50 hover:bg-bg-tertiary/30 cursor-pointer"
                onClick={() => router.push(`/report/validation/${v.id}`)}
              >
                <td className="p-2" onClick={(e) => e.stopPropagation()}>
                  <input
                    type="checkbox"
                    checked={selected.has(v.id)}
                    onChange={() => toggleSelect(v.id)}
                    disabled={!selected.has(v.id) && selected.size >= 4}
                    className="accent-accent-blue"
                  />
                </td>
                <td className="p-2 text-text-secondary font-mono">{v.id.slice(0, 8)}</td>
                <td className="p-2 text-text-primary">
                  {v.strategyName}
                  {v.strategyVersion && (
                    <span className="text-text-muted ml-1">v{v.strategyVersion}</span>
                  )}
                </td>
                <td className="p-2 text-text-secondary">{v.thresholdProfileName}</td>
                <td className="p-2 text-right text-text-primary font-mono">
                  {v.status === "Completed" ? v.compositeScore.toFixed(1) : "\u2014"}
                </td>
                <td className="p-2 text-center">
                  {v.status === "Completed" ? (
                    <VerdictBadge verdict={v.verdict} />
                  ) : (
                    <span className="text-xs text-text-muted">{v.status}</span>
                  )}
                </td>
                <td className="p-2 text-right text-text-secondary">
                  {formatNumber(v.candidatesOut, 0)}/{formatNumber(v.candidatesIn, 0)}
                </td>
                <td className="p-2 text-right text-text-muted">{formatDuration(v.durationMs)}</td>
                <td className="p-2 text-text-muted">{new Date(v.startedAt).toLocaleDateString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
