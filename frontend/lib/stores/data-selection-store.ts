// Sidebar selection state. In-memory only — the sidebar resets on navigation away
// from the Data tab.

import { create } from "zustand";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

export type SidebarMode = "view" | null;

interface SidebarSelection {
  mode: SidebarMode;
  exchange: string | null;
  asset: AssetCatalogEntry | null;
  /** "view" → feed being inspected. */
  feed: FeedCatalogEntry | null;
}

interface DataSelectionStore extends SidebarSelection {
  openView: (exchange: string, asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
  close: () => void;
}

export const useDataSelectionStore = create<DataSelectionStore>((set) => ({
  mode: null,
  exchange: null,
  asset: null,
  feed: null,
  openView: (exchange, asset, feed) =>
    set({ mode: "view", exchange, asset, feed }),
  close: () => set({ mode: null, exchange: null, asset: null, feed: null }),
}));
