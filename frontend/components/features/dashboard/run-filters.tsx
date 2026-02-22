"use client";

// T054 - Run filters component

interface FilterValues {
  assetName: string;
  exchange: string;
  timeFrame: string;
  from: string;
  to: string;
}

interface RunFiltersProps {
  filters: FilterValues;
  onChange: (filters: FilterValues) => void;
}

export type { FilterValues };

export function RunFilters({ filters, onChange }: RunFiltersProps) {
  const update = (key: keyof FilterValues, value: string) => {
    onChange({ ...filters, [key]: value });
  };

  return (
    <div className="flex flex-wrap items-end gap-3">
      <div className="space-y-1">
        <label htmlFor="filter-asset" className="text-xs text-text-muted">Asset</label>
        <input
          id="filter-asset"
          type="text"
          placeholder="e.g. BTCUSDT"
          value={filters.assetName}
          onChange={(e) => update("assetName", e.target.value)}
          className="w-32 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
        />
      </div>
      <div className="space-y-1">
        <label htmlFor="filter-exchange" className="text-xs text-text-muted">Exchange</label>
        <input
          id="filter-exchange"
          type="text"
          placeholder="e.g. Binance"
          value={filters.exchange}
          onChange={(e) => update("exchange", e.target.value)}
          className="w-28 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
        />
      </div>
      <div className="space-y-1">
        <label htmlFor="filter-timeframe" className="text-xs text-text-muted">Timeframe</label>
        <input
          id="filter-timeframe"
          type="text"
          placeholder="e.g. 00:15:00"
          value={filters.timeFrame}
          onChange={(e) => update("timeFrame", e.target.value)}
          className="w-28 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
        />
      </div>
      <div className="space-y-1">
        <label htmlFor="filter-from" className="text-xs text-text-muted">From</label>
        <input
          id="filter-from"
          type="date"
          value={filters.from}
          onChange={(e) => update("from", e.target.value)}
          className="px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary"
        />
      </div>
      <div className="space-y-1">
        <label htmlFor="filter-to" className="text-xs text-text-muted">To</label>
        <input
          id="filter-to"
          type="date"
          value={filters.to}
          onChange={(e) => update("to", e.target.value)}
          className="px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary"
        />
      </div>
    </div>
  );
}
