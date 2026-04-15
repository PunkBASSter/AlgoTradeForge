"use client";

// T057 - DssBuilder — collapsible section for building subscriptionAxis rows

import { useState, useCallback } from "react";
import { ChevronIcon } from "@/components/ui/chevron-icon";
import type { DataSubscription } from "@/types/api";

interface DssRow {
  assetName: string;
  exchange: string;
  timeFrame: string;
}

function emptyRow(): DssRow {
  return { assetName: "", exchange: "", timeFrame: "" };
}

interface DssBuilderProps {
  /** Current subscriptionAxis value — each inner array is one DSS (set of subscriptions). */
  value: DataSubscription[][];
  /** Called when the subscriptionAxis changes. */
  onChange: (subscriptionAxis: DataSubscription[][]) => void;
}

export function DssBuilder({ value, onChange }: DssBuilderProps) {
  const [expanded, setExpanded] = useState(false);

  // Convert DataSubscription[][] to flat DssRow[] for editing (one row per DSS).
  // Each DSS is a single-element array [{ assetName, exchange, timeFrame }].
  const rows: DssRow[] = value.map((dss) => ({
    assetName: dss[0]?.assetName ?? "",
    exchange: dss[0]?.exchange ?? "",
    timeFrame: dss[0]?.timeFrame ?? "",
  }));

  const emitChange = useCallback(
    (newRows: DssRow[]) => {
      const axis: DataSubscription[][] = newRows
        .filter((r) => r.assetName.trim() || r.exchange.trim() || r.timeFrame.trim())
        .map((r) => [
          {
            assetName: r.assetName.trim(),
            exchange: r.exchange.trim(),
            timeFrame: r.timeFrame.trim(),
          },
        ]);
      onChange(axis);
    },
    [onChange],
  );

  const handleFieldChange = useCallback(
    (index: number, field: keyof DssRow, newValue: string) => {
      const updated = [...rows];
      updated[index] = { ...updated[index], [field]: newValue };
      emitChange(updated);
    },
    [rows, emitChange],
  );

  const handleAddRow = useCallback(() => {
    emitChange([...rows, emptyRow()]);
  }, [rows, emitChange]);

  const handleRemoveRow = useCallback(
    (index: number) => {
      const updated = rows.filter((_, i) => i !== index);
      emitChange(updated);
    },
    [rows, emitChange],
  );

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel">
      {/* Collapsible header */}
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="w-full flex items-center gap-2 p-3 text-left hover:bg-bg-hover transition-colors rounded-t-lg"
      >
        <ChevronIcon expanded={expanded} />
        <span className="text-sm font-medium text-text-primary">
          DSS Builder
        </span>
        <span className="text-xs text-text-muted ml-auto">
          {rows.length} {rows.length === 1 ? "subscription" : "subscriptions"}
        </span>
      </button>

      {/* Body */}
      {expanded && (
        <div className="border-t border-border-default p-3 space-y-3">
          {/* Rows table */}
          {rows.length > 0 && (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-xs font-medium uppercase tracking-wider text-text-muted">
                    <th className="pb-2 pr-2 text-left">Asset Name</th>
                    <th className="pb-2 pr-2 text-left">Exchange</th>
                    <th className="pb-2 pr-2 text-left">Time Frame</th>
                    <th className="pb-2 w-8" />
                  </tr>
                </thead>
                <tbody>
                  {rows.map((row, index) => (
                    <tr key={`${index}-${row.assetName}-${row.exchange}-${row.timeFrame}`}>
                      <td className="pr-2 pb-2">
                        <input
                          type="text"
                          value={row.assetName}
                          onChange={(e) => handleFieldChange(index, "assetName", e.target.value)}
                          placeholder="BTCUSDT"
                          className="w-full rounded-md border border-border-default bg-bg-base px-2 py-1.5 text-sm text-text-primary placeholder:text-text-muted/50 focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue"
                        />
                      </td>
                      <td className="pr-2 pb-2">
                        <input
                          type="text"
                          value={row.exchange}
                          onChange={(e) => handleFieldChange(index, "exchange", e.target.value)}
                          placeholder="Binance"
                          className="w-full rounded-md border border-border-default bg-bg-base px-2 py-1.5 text-sm text-text-primary placeholder:text-text-muted/50 focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue"
                        />
                      </td>
                      <td className="pr-2 pb-2">
                        <input
                          type="text"
                          value={row.timeFrame}
                          onChange={(e) => handleFieldChange(index, "timeFrame", e.target.value)}
                          placeholder="01:00:00"
                          className="w-full rounded-md border border-border-default bg-bg-base px-2 py-1.5 text-sm text-text-primary placeholder:text-text-muted/50 focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue"
                        />
                      </td>
                      <td className="pb-2">
                        <button
                          type="button"
                          onClick={() => handleRemoveRow(index)}
                          className="p-1 rounded hover:bg-bg-hover text-text-muted hover:text-accent-red transition-colors"
                          title="Remove row"
                        >
                          <svg
                            width="14"
                            height="14"
                            viewBox="0 0 14 14"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="1.5"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                          >
                            <path d="M3 3l8 8M11 3l-8 8" />
                          </svg>
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {/* Add row button */}
          <button
            type="button"
            onClick={handleAddRow}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md border border-border-default bg-bg-surface hover:bg-bg-hover text-text-muted hover:text-text-primary transition-colors"
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 14 14"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.5"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="M7 3v8M3 7h8" />
            </svg>
            Add Row
          </button>
        </div>
      )}
    </div>
  );
}
