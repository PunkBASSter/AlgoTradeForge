"use client";

// Searchable single-select over the FULL on-disk catalog (crypto + equity + paid feeds).
// Replaces the exchange→asset cascade: one text query filters across symbol + exchange +
// type. The catalog is fetched once and cached (refresh via the Data page / refreshCatalog).

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import type { AssetCatalogEntry } from "@/types/data-tab";

interface AssetComboboxProps {
  value: { exchange: string; symbol: string } | null;
  onSelect: (entry: AssetCatalogEntry) => void;
  disabled?: boolean;
}

const MAX_RESULTS = 50;

const INPUT_CLASSES =
  "w-full rounded-md border border-border-default bg-bg-base px-2 py-1.5 text-sm text-text-primary " +
  "focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue " +
  "disabled:opacity-50 disabled:cursor-not-allowed";

export function AssetCombobox({ value, onSelect, disabled }: AssetComboboxProps) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);

  const assetsQuery = useQuery({
    queryKey: ["data", "assets"],
    queryFn: ({ signal }) => dataApi.getAssets(signal),
    staleTime: Infinity,
  });

  const matches = useMemo(() => {
    const all = assetsQuery.data?.assets ?? [];
    const q = query.trim().toLowerCase();
    if (!q) return all.slice(0, MAX_RESULTS);
    return all
      .filter(
        (a) =>
          a.display_name.toLowerCase().includes(q) ||
          a.symbol.toLowerCase().includes(q) ||
          a.exchange.toLowerCase().includes(q) ||
          a.type.toLowerCase().includes(q),
      )
      .slice(0, MAX_RESULTS);
  }, [assetsQuery.data, query]);

  const selectedLabel = value ? `${value.symbol} · ${value.exchange}` : "";

  return (
    <div className="relative">
      <label className="block text-xs font-medium uppercase tracking-wider text-text-muted mb-1">
        Asset
      </label>
      <input
        role="combobox"
        aria-expanded={open}
        aria-label="Asset"
        className={INPUT_CLASSES}
        placeholder={assetsQuery.isLoading ? "Loading catalog…" : "Search symbol or exchange…"}
        value={open ? query : selectedLabel}
        disabled={disabled || assetsQuery.isLoading}
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          setQuery(e.target.value);
          setOpen(true);
        }}
        onBlur={() => setTimeout(() => setOpen(false), 120)}
      />
      {open && matches.length > 0 && (
        <ul
          className="absolute z-10 mt-1 max-h-64 w-full overflow-auto rounded-md border border-border-default bg-bg-panel shadow-lg"
          role="listbox"
        >
          {matches.map((a) => (
            <li key={`${a.exchange}|${a.symbol}`} role="option" aria-selected={false}>
              <button
                type="button"
                className="flex w-full items-center justify-between gap-2 px-2 py-1.5 text-left text-sm text-text-primary hover:bg-bg-base"
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => {
                  onSelect(a);
                  setQuery("");
                  setOpen(false);
                }}
              >
                <span className="font-medium">{a.display_name}</span>
                <span className="text-xs text-text-muted">
                  {a.exchange} · {a.type}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
