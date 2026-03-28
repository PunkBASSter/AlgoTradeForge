"use client";

import { useMemo } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useStrategies } from "@/hooks/use-strategies";
import { useAvailableStrategies } from "@/hooks/use-available-strategies";

interface StrategySelectorProps {
  selected: string | null;
}

export function StrategySelector({ selected }: StrategySelectorProps) {
  const { data: ranStrategies } = useStrategies();
  const { data: availableStrategies, isLoading } = useAvailableStrategies();
  const pathname = usePathname();

  // Merge: recency-sorted strategies with runs first, then any never-run strategies
  const strategies = useMemo(() => {
    const ran = ranStrategies ?? [];
    const available = availableStrategies?.map((s) => s.name) ?? [];
    const ranSet = new Set(ran);
    return [...ran, ...available.filter((name) => !ranSet.has(name))];
  }, [ranStrategies, availableStrategies]);

  // Derive mode from current URL
  const mode = pathname.endsWith("/optimization")
    ? "optimization"
    : pathname.endsWith("/live")
      ? "live"
      : "backtest";

  return (
    <div className="space-y-1">
      {isLoading && (
        <div className="px-3 py-2 text-sm text-text-muted">Loading...</div>
      )}
      {strategies.map((name) => (
        <Link
          key={name}
          href={`/${name}/${mode}`}
          className={`block w-full text-left px-3 py-2 text-sm rounded transition-colors ${
            selected === name
              ? "bg-accent-blue text-white"
              : "text-text-secondary hover:bg-bg-hover hover:text-text-primary"
          }`}
        >
          {name}
        </Link>
      ))}
    </div>
  );
}
