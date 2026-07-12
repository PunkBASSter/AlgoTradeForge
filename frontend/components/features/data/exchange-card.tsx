"use client";

// Per-exchange expandable card. Collapsed by default; expanding fetches the asset list.
// The grid is mounted lazily so virtualization doesn't allocate when closed.

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import type { ExchangeSummary } from "@/types/data-tab";
import { AssetFeedGrid } from "./asset-feed-grid";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";

interface Props {
  exchange: ExchangeSummary;
}

export function ExchangeCard({ exchange }: Props) {
  const [expanded, setExpanded] = useState(false);
  const openView = useDataSelectionStore((s) => s.openView);

  const { data, isLoading, error } = useQuery({
    queryKey: ["data", "exchange-assets", exchange.name],
    queryFn: ({ signal }) => dataApi.getAssetsByExchange(exchange.name, signal),
    enabled: expanded,
  });

  return (
    <div className="border border-border-default rounded bg-bg-surface">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="w-full flex items-center justify-between px-4 py-3 hover:bg-bg-hover transition-colors"
        aria-expanded={expanded}
      >
        <div className="flex items-center gap-3">
          <span className="text-text-secondary text-sm">{expanded ? "▾" : "▸"}</span>
          <span className="font-semibold text-text-primary">{exchange.name}</span>
          <span className="text-text-muted text-sm">
            {exchange.asset_count} {exchange.asset_count === 1 ? "asset" : "assets"}
          </span>
        </div>
      </button>

      {expanded && (
        <div className="border-t border-border-subtle px-4 py-3">
          {isLoading && (
            <div className="text-text-secondary text-sm">Loading assets…</div>
          )}
          {error && (
            <div className="text-accent-red text-sm">
              Failed to load: {error instanceof Error ? error.message : String(error)}
            </div>
          )}
          {data && data.assets.length === 0 && (
            <div className="text-text-secondary text-sm">No assets in this exchange.</div>
          )}
          {data && data.assets.length > 0 && (
            <AssetFeedGrid
              assets={data.assets}
              onView={(asset, feed) => openView(exchange.name, asset, feed)}
            />
          )}
        </div>
      )}
    </div>
  );
}
