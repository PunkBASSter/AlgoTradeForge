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

  const { matches, total } = useMemo(() => {
    const all = assetsQuery.data?.assets ?? [];
    const q = query.trim().toLowerCase();
    if (!q) return { matches: all.slice(0, MAX_RESULTS), total: all.length };

    const filtered = all.filter(
      (a) =>
        a.display_name.toLowerCase().includes(q) ||
        a.symbol.toLowerCase().includes(q) ||
        a.exchange.toLowerCase().includes(q) ||
        a.type.toLowerCase().includes(q),
    );
    // Rank so an exact / prefix ticker surfaces above the MAX_RESULTS cap even when many
    // other names merely contain the substring (stable sort preserves catalog order within a rank).
    const rank = (a: AssetCatalogEntry) => {
      const s = a.symbol.toLowerCase();
      if (s === q) return 0;
      if (s.startsWith(q)) return 1;
      if (s.includes(q)) return 2;
      return 3;
    };
    filtered.sort((a, b) => rank(a) - rank(b));
    return { matches: filtered.slice(0, MAX_RESULTS), total: filtered.length };
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
        aria-controls="asset-combobox-list"
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
          id="asset-combobox-list"
          role="listbox"
        >
          {matches.map((a) => (
            <li key={`${a.exchange}|${a.symbol}`} role="option" aria-selected={value?.exchange === a.exchange && value?.symbol === a.symbol}>
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
          {total > matches.length && (
            <li className="border-t border-border-default px-2 py-1.5 text-xs text-text-muted">
              Showing {matches.length} of {total} — keep typing to narrow
            </li>
          )}
        </ul>
      )}
    </div>
  );
}
