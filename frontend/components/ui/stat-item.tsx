import type { ReactNode } from "react";

export function StatItem({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div>
      <span className="text-xs font-medium uppercase tracking-wider text-text-muted">
        {label}
      </span>
      <p className="text-lg font-semibold text-text-primary mt-1">{value}</p>
    </div>
  );
}
