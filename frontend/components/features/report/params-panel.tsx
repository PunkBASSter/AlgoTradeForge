// T042 - ParamsPanel component

import { toTitleCase } from "@/lib/utils/format";

interface ParamsPanelProps {
  parameters: Record<string, unknown>;
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return "null";
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return JSON.stringify(value);
}

export function ParamsPanel({ parameters }: ParamsPanelProps) {
  const entries = Object.entries(parameters);

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel p-4">
      <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
        Parameters
      </h3>
      <div className="space-y-2">
        {entries.map(([key, value]) => (
          <div key={key} className="flex justify-between gap-2">
            <span className="text-sm text-text-secondary truncate">
              {toTitleCase(key)}
            </span>
            <span className="text-sm font-medium text-text-primary whitespace-nowrap font-mono">
              {formatValue(value)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
