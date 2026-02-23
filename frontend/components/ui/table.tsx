"use client";

import { type ReactNode } from "react";

export interface Column<T> {
  key: string;
  header: string;
  render?: (value: unknown, row: T) => ReactNode;
  className?: string;
}

interface TableProps<T extends object> {
  columns: Column<T>[];
  data: T[];
  onRowClick?: (row: T) => void;
  rowKey: keyof T & string;
  emptyMessage?: string;
  testId?: string;
}

export function Table<T extends object>({
  columns,
  data,
  onRowClick,
  rowKey,
  emptyMessage = "No data available",
  testId,
}: TableProps<T>) {
  return (
    <div className="overflow-x-auto rounded-md border border-border-default" data-testid={testId}>
      <table className="w-full text-left text-sm">
        <thead>
          <tr className="border-b border-border-default bg-bg-panel">
            {columns.map((col) => (
              <th
                key={col.key}
                className={`px-4 py-3 text-xs font-medium uppercase tracking-wider text-text-muted ${col.className ?? ""}`}
              >
                {col.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {data.length === 0 ? (
            <tr>
              <td
                colSpan={columns.length}
                className="px-4 py-8 text-center text-text-muted"
              >
                {emptyMessage}
              </td>
            </tr>
          ) : (
            data.map((row) => {
              const rec = row as Record<string, unknown>;
              return (
                <tr
                  key={String(rec[rowKey])}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  className={`bg-bg-surface transition-colors hover:bg-bg-hover ${
                    onRowClick ? "cursor-pointer" : ""
                  }`}
                >
                  {columns.map((col) => (
                    <td
                      key={col.key}
                      className={`px-4 py-3 text-text-primary ${col.className ?? ""}`}
                    >
                      {col.render
                        ? col.render(rec[col.key], row)
                        : String(rec[col.key] ?? "")}
                    </td>
                  ))}
                </tr>
              );
            })
          )}
        </tbody>
      </table>
    </div>
  );
}
