"use client";

import type { CollectionGroupSummary, DesiredStateReport } from "@/types/data-tab";
import { Button } from "@/components/ui/button";

interface GroupCardProps {
  summary: CollectionGroupSummary;
  desiredState: DesiredStateReport | undefined;
  onEdit: () => void;
  onDelete: () => void;
}

export function GroupCard({ summary, desiredState, onEdit, onDelete }: GroupCardProps) {
  const groupTuples = desiredState?.tuples.filter((t) =>
    t.groups.includes(summary.name),
  ) ?? [];

  const materialized = groupTuples.filter((t) => t.status === "materialized").length;
  const partial = groupTuples.filter((t) => t.status === "partial").length;
  const missing = groupTuples.filter((t) => t.status === "missing").length;
  const onDemand = groupTuples.filter((t) => t.status === "on-demand").length;

  return (
    <div className="border border-border-default rounded bg-bg-surface px-4 py-3 space-y-2">
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-semibold text-text-primary">{summary.name}</span>
          {summary.enabled ? (
            <span className="text-xs bg-accent-green/15 text-accent-green border border-accent-green/30 rounded px-1.5 py-0.5">
              enabled
            </span>
          ) : (
            <span className="text-xs bg-bg-hover text-text-muted border border-border-subtle rounded px-1.5 py-0.5">
              disabled
            </span>
          )}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <Button variant="ghost" onClick={onEdit} className="text-xs px-2 py-1">
            Edit
          </Button>
          <Button variant="danger" onClick={onDelete} className="text-xs px-2 py-1">
            Delete
          </Button>
        </div>
      </div>

      <div className="flex flex-wrap gap-1.5">
        {summary.exchanges.map((ex) => (
          <span
            key={ex}
            className="text-xs bg-bg-hover text-text-secondary border border-border-subtle rounded px-1.5 py-0.5"
          >
            {ex}
          </span>
        ))}
        <span className="text-xs text-text-muted">
          {summary.symbol_count} {summary.symbol_count === 1 ? "symbol" : "symbols"}
        </span>
        <span className="text-xs text-text-muted">·</span>
        <span className="text-xs text-text-muted">
          {summary.feed_count} {summary.feed_count === 1 ? "feed" : "feeds"}
        </span>
      </div>

      {desiredState !== undefined && (
        <div className="flex flex-wrap gap-2 pt-1">
          {materialized > 0 && (
            <span className="text-xs text-accent-green">
              {materialized} materialized
            </span>
          )}
          {partial > 0 && (
            <span className="text-xs text-accent-yellow">
              {partial} partial
            </span>
          )}
          {missing > 0 && (
            <span className="text-xs text-accent-red">
              {missing} missing
            </span>
          )}
          {onDemand > 0 && (
            <span className="text-xs text-text-muted">
              {onDemand} on-demand
            </span>
          )}
          {groupTuples.length === 0 && (
            <span className="text-xs text-text-muted">no tuples in desired state</span>
          )}
        </div>
      )}
    </div>
  );
}
