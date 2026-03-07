import type { ReactNode } from "react";

const presetColors: Record<string, string> = {
  Pending: "bg-yellow-900/30 text-yellow-400 border-yellow-700",
  Running: "bg-green-900/30 text-green-400 border-green-700",
  Completed: "bg-green-900/30 text-green-400 border-green-700",
  Failed: "bg-red-900/30 text-red-400 border-red-700",
  Cancelled: "bg-neutral-800 text-text-muted border-border-default",
  Connecting: "bg-yellow-900/30 text-yellow-400 border-yellow-700",
  Idle: "bg-neutral-800 text-text-muted border-border-default",
  Stopping: "bg-yellow-900/30 text-yellow-400 border-yellow-700",
  Stopped: "bg-red-900/30 text-red-400 border-red-700",
  Error: "bg-red-900/30 text-red-400 border-red-700",
};

const fallback = "bg-neutral-800 text-text-muted border-border-default";

export function StatusBadge({ status }: { status: string }) {
  const cls = presetColors[status] ?? fallback;
  return (
    <span
      data-testid="status-badge"
      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${cls}`}
    >
      {status}
    </span>
  );
}
