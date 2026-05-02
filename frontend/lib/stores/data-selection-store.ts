// Phase 3 — sidebar selection state. The asset×feed grid sets the active selection on
// cell click; the sidebar reads it to render the Status card or new-aggregate form.
//
// Two selection modes:
//   - "view"   — viewing an existing feed (Status card visible).
//   - "create" — creating a new aggregated feed (new-aggregate form visible).
//
// State is in-memory only — not persisted (the sidebar resets on navigation away from
// the Data tab).

import { create } from "zustand";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

export type SidebarMode = "view" | "create" | null;

interface SidebarSelection {
  mode: SidebarMode;
  exchange: string | null;
  asset: AssetCatalogEntry | null;
  // For "view": the existing feed being inspected.
  // For "create": the SOURCE feed driving the aggregate (e.g. "1m" or "ticks").
  feed: FeedCatalogEntry | null;
}

interface DataSelectionStore extends SidebarSelection {
  openView: (exchange: string, asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
  openCreate: (exchange: string, asset: AssetCatalogEntry, sourceFeed: FeedCatalogEntry) => void;
  close: () => void;
}

export const useDataSelectionStore = create<DataSelectionStore>((set) => ({
  mode: null,
  exchange: null,
  asset: null,
  feed: null,
  openView: (exchange, asset, feed) =>
    set({ mode: "view", exchange, asset, feed }),
  openCreate: (exchange, asset, sourceFeed) =>
    set({ mode: "create", exchange, asset, feed: sourceFeed }),
  close: () => set({ mode: null, exchange: null, asset: null, feed: null }),
}));
