// T042 - ParamsPanel component

"use client";

import { useToast } from "@/components/ui/toast";
import { toTitleCase } from "@/lib/utils/format";

const INTERNAL_PARAM_KEYS = new Set(["DataSubscriptions"]);

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
  const { toast } = useToast();
  const entries = Object.entries(parameters);

  const handleCopy = () => {
    const filtered = Object.fromEntries(
      Object.entries(parameters).filter(([k]) => !INTERNAL_PARAM_KEYS.has(k)),
    );
    navigator.clipboard.writeText(JSON.stringify(filtered, null, 2));
    toast("Parameters copied", "success");
  };

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel p-4">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
          Parameters
        </h3>
        <button
          onClick={handleCopy}
          className="p-1 rounded hover:bg-bg-surface text-text-muted hover:text-text-primary transition-colors"
          title="Copy parameters to clipboard"
        >
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <rect x="5.5" y="5.5" width="8" height="8" rx="1" />
            <path d="M10.5 5.5V3.5a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2" />
          </svg>
        </button>
      </div>
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
