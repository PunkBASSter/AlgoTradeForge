"use client";

import type { ValidatePreview } from "@/types/data-tab";

interface ValidatePreviewPanelProps {
  preview: ValidatePreview;
}

export function ValidatePreviewPanel({ preview }: ValidatePreviewPanelProps) {
  const { expansion } = preview;

  return (
    <div className="border border-border-default rounded bg-bg-hover p-3 space-y-3 text-sm">
      <div className="text-text-secondary">
        <span className="font-semibold text-text-primary">{expansion.tuple_count} tuples</span>{" "}
        expanded
        {expansion.already_materialized > 0 && (
          <span className="text-text-muted ml-2">
            ({expansion.already_materialized} already materialized)
          </span>
        )}
      </div>

      {expansion.per_exchange.length > 0 && (
        <div className="space-y-1">
          <div className="text-xs text-text-muted uppercase tracking-wide">Per exchange</div>
          {expansion.per_exchange.map((e) => (
            <div key={e.exchange} className="text-text-secondary">
              <span className="text-text-primary font-medium">{e.exchange}:</span>{" "}
              {e.symbols} {e.symbols === 1 ? "symbol" : "symbols"} × {e.feeds}{" "}
              {e.feeds === 1 ? "feed" : "feeds"}
            </div>
          ))}
        </div>
      )}

      {expansion.unsupported.length > 0 && (
        <div className="space-y-1">
          <div className="text-xs text-accent-yellow uppercase tracking-wide">Unsupported</div>
          {expansion.unsupported.map((u, i) => (
            <div key={i} className="text-text-muted text-xs">
              {u.exchange}/{u.canonical}: {u.reason}
            </div>
          ))}
        </div>
      )}

      {expansion.conflicts.length > 0 && (
        <div className="space-y-1">
          <div className="text-xs text-accent-red uppercase tracking-wide">Conflicts</div>
          {expansion.conflicts.map((c, i) => (
            <div key={i} role="alert" className="text-accent-red text-xs">
              {c.message}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
