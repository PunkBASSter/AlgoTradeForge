"use client";

// T053 - Strategy selector sidebar component (URL-driven)

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAvailableStrategies } from "@/hooks/use-available-strategies";

interface StrategySelectorProps {
  selected: string | null;
}

export function StrategySelector({ selected }: StrategySelectorProps) {
  const { data: strategies, isLoading } = useAvailableStrategies();
  const pathname = usePathname();

  // Derive mode from current URL: /strategy/backtest or /strategy/optimization
  const mode = pathname.endsWith("/optimization") ? "optimization" : "backtest";

  return (
    <div className="space-y-1">
      <h3 className="text-xs font-semibold text-text-muted uppercase tracking-wider px-3 mb-2">
        Strategies
      </h3>
      <Link
        href={`/all/${mode}`}
        className={`block w-full text-left px-3 py-2 text-sm rounded transition-colors ${
          selected === null
            ? "bg-accent-blue text-white"
            : "text-text-secondary hover:bg-bg-hover hover:text-text-primary"
        }`}
      >
        All Strategies
      </Link>
      {isLoading && (
        <div className="px-3 py-2 text-sm text-text-muted">Loading...</div>
      )}
      {strategies?.map((s) => (
        <Link
          key={s.name}
          href={`/${s.name}/${mode}`}
          className={`block w-full text-left px-3 py-2 text-sm rounded transition-colors ${
            selected === s.name
              ? "bg-accent-blue text-white"
              : "text-text-secondary hover:bg-bg-hover hover:text-text-primary"
          }`}
        >
          {s.name}
        </Link>
      ))}
    </div>
  );
}
