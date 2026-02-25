"use client";

// T053 - Strategy selector sidebar component

import { useAvailableStrategies } from "@/hooks/use-available-strategies";

interface StrategySelectorProps {
  selected: string | null;
  onSelect: (name: string | null) => void;
}

export function StrategySelector({
  selected,
  onSelect,
}: StrategySelectorProps) {
  const { data: strategies, isLoading } = useAvailableStrategies();

  return (
    <div className="space-y-1">
      <h3 className="text-xs font-semibold text-text-muted uppercase tracking-wider px-3 mb-2">
        Strategies
      </h3>
      <button
        onClick={() => onSelect(null)}
        className={`w-full text-left px-3 py-2 text-sm rounded transition-colors ${
          selected === null
            ? "bg-accent-blue text-white"
            : "text-text-secondary hover:bg-bg-hover hover:text-text-primary"
        }`}
      >
        All Strategies
      </button>
      {isLoading && (
        <div className="px-3 py-2 text-sm text-text-muted">Loading...</div>
      )}
      {strategies?.map((s) => (
        <button
          key={s.name}
          onClick={() => onSelect(s.name)}
          className={`w-full text-left px-3 py-2 text-sm rounded transition-colors ${
            selected === s.name
              ? "bg-accent-blue text-white"
              : "text-text-secondary hover:bg-bg-hover hover:text-text-primary"
          }`}
        >
          {s.name}
        </button>
      ))}
    </div>
  );
}
