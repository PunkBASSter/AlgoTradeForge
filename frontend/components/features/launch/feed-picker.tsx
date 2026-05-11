"use client";

// Catalog-fed cascading dropdowns (exchange → asset → feed) for picking ONE
// DataFeedSubscription. The feed list is filtered by role-eligibility (Primary slots
// see TimeBar/AltBar/Tick; Side slots see Side feeds incl. alt-bar sidecars).

import { useEffect, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import { compareFeed } from "@/lib/data/feed-order";
import {
  feedToSubscription,
  formatFeedLabel,
  isFeedEligibleForRole,
} from "@/lib/data/feed-label";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";
import type { DataFeedRole, DataFeedSubscription } from "@/types/api";

interface FeedPickerProps {
  /** Picker emits a Primary or Side subscription per this role. Filters eligibility. */
  role: DataFeedRole;
  /** Current selection state — `null` means "no feed picked yet". */
  value: FeedPickerSelection | null;
  /** Called whenever the user changes any of the three selects. */
  onChange: (sel: FeedPickerSelection | null) => void;
  /** Optional: hide a specific feed-id from the feed dropdown (e.g. dedup against chips). */
  excludeFeedIds?: ReadonlySet<string>;
  /** Disabled state — used by parents during submit. */
  disabled?: boolean;
}

export interface FeedPickerSelection {
  exchange: string;
  asset: string;
  feedId: string;
  /** Resolved subscription. Non-null only when all three selects are populated. */
  subscription: DataFeedSubscription | null;
}

const SELECT_CLASSES =
  "w-full rounded-md border border-border-default bg-bg-base px-2 py-1.5 text-sm text-text-primary " +
  "focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue " +
  "disabled:opacity-50 disabled:cursor-not-allowed";

export function FeedPicker({
  role,
  value,
  onChange,
  excludeFeedIds,
  disabled,
}: FeedPickerProps) {
  const exchangesQuery = useQuery({
    queryKey: ["data", "exchanges"],
    queryFn: ({ signal }) => dataApi.getExchanges(signal),
  });

  const assetsQuery = useQuery({
    queryKey: ["data", "exchange-assets", value?.exchange ?? ""],
    queryFn: ({ signal }) => dataApi.getAssetsByExchange(value!.exchange, signal),
    enabled: !!value?.exchange,
  });

  const selectedAsset: AssetCatalogEntry | null = useMemo(() => {
    if (!value?.asset || !assetsQuery.data) return null;
    return assetsQuery.data.assets.find((a) => a.symbol === value.asset) ?? null;
  }, [value?.asset, assetsQuery.data]);

  const eligibleFeeds: FeedCatalogEntry[] = useMemo(() => {
    if (!selectedAsset) return [];
    return selectedAsset.feeds
      .filter((f) => isFeedEligibleForRole(f, role))
      .filter((f) => !excludeFeedIds?.has(f.id))
      .sort(compareFeed);
  }, [selectedAsset, role, excludeFeedIds]);

  // Clear the feed selection when the previously chosen feedId is no longer in the
  // eligible list (e.g. asset changed). The `!value.feedId` guard avoids re-firing
  // once we've already cleared.
  useEffect(() => {
    if (!value || !selectedAsset) return;
    if (!value.feedId) return;
    if (!eligibleFeeds.some((f) => f.id === value.feedId)) {
      onChange({ ...value, feedId: "", subscription: null });
    }
  }, [eligibleFeeds, value, selectedAsset, onChange]);

  const handleExchange = (next: string) => {
    if (!next) { onChange(null); return; }
    onChange({
      exchange: next,
      asset: "",
      feedId: "",
      subscription: null,
    });
  };

  const handleAsset = (next: string) => {
    if (!value?.exchange) return;
    if (!next) {
      onChange({
        exchange: value.exchange,
        asset: "",
        feedId: "",
        subscription: null,
      });
      return;
    }
    onChange({
      exchange: value.exchange,
      asset: next,
      feedId: "",
      subscription: null,
    });
  };

  const handleFeed = (feedId: string) => {
    if (!value?.exchange || !value?.asset || !selectedAsset || !feedId) {
      if (value?.exchange && value?.asset) {
        onChange({
          exchange: value.exchange,
          asset: value.asset,
          feedId: "",
          subscription: null,
        });
      }
      return;
    }
    const feed = selectedAsset.feeds.find((f) => f.id === feedId);
    if (!feed) return;
    const sub = feedToSubscription(value.asset, value.exchange, feed, role);
    if (!sub) return;
    onChange({
      exchange: value.exchange,
      asset: value.asset,
      feedId,
      subscription: sub,
    });
  };

  return (
    <div className="grid grid-cols-1 sm:grid-cols-3 gap-2" role="group" aria-label="Feed picker">
      <div>
        <label className="block text-xs font-medium uppercase tracking-wider text-text-muted mb-1">
          Exchange
        </label>
        <select
          className={SELECT_CLASSES}
          value={value?.exchange ?? ""}
          onChange={(e) => handleExchange(e.target.value)}
          disabled={disabled || exchangesQuery.isLoading}
          aria-label="Exchange"
        >
          <option value="">{exchangesQuery.isLoading ? "Loading…" : "Select exchange"}</option>
          {exchangesQuery.data?.exchanges.map((ex) => (
            <option key={ex.name} value={ex.name}>
              {ex.name} ({ex.asset_count})
            </option>
          ))}
        </select>
      </div>

      <div>
        <label className="block text-xs font-medium uppercase tracking-wider text-text-muted mb-1">
          Asset
        </label>
        <select
          className={SELECT_CLASSES}
          value={value?.asset ?? ""}
          onChange={(e) => handleAsset(e.target.value)}
          disabled={disabled || !value?.exchange || assetsQuery.isLoading}
          aria-label="Asset"
        >
          <option value="">
            {!value?.exchange
              ? "Pick an exchange first"
              : assetsQuery.isLoading
                ? "Loading…"
                : "Select asset"}
          </option>
          {assetsQuery.data?.assets
            .slice()
            .sort((a, b) => a.display_name.localeCompare(b.display_name))
            .map((a) => (
              <option key={a.symbol} value={a.symbol}>
                {a.display_name} {a.type ? `(${a.type})` : ""}
              </option>
            ))}
        </select>
      </div>

      <div>
        <label className="block text-xs font-medium uppercase tracking-wider text-text-muted mb-1">
          Feed
        </label>
        <select
          className={SELECT_CLASSES}
          value={value?.feedId ?? ""}
          onChange={(e) => handleFeed(e.target.value)}
          disabled={disabled || !selectedAsset || eligibleFeeds.length === 0}
          aria-label="Feed"
        >
          <option value="">
            {!selectedAsset
              ? "Pick an asset first"
              : eligibleFeeds.length === 0
                ? `No ${role === "Primary" ? "primary" : "side"} feeds available`
                : "Select feed"}
          </option>
          {eligibleFeeds.map((f) => (
            <option key={f.id} value={f.id}>
              {formatFeedLabel(f)}
              {f.sidecar ? "  •" : ""}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}
