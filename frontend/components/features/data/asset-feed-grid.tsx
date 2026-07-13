"use client";

// Both-axis virtualized asset×feed grid. Two `useVirtualizer` instances share a single
// scroll container so scroll state stays consistent. At 500 assets × 20 feeds only a
// viewport-window of cells mounts.

import { useMemo, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";
import { unionFeedColumns } from "@/lib/data/feed-order";
import { FeedCell } from "./feed-cell";

interface Props {
  assets: AssetCatalogEntry[];
  rowHeight?: number;    // px; defaults to 36
  colWidth?: number;     // px; defaults to 132
  onView?: (asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
}

const DEFAULT_ROW_HEIGHT = 36;
const DEFAULT_COL_WIDTH = 132;
const ROW_HEADER_WIDTH = 160;     // pixel width of the asset-name header column

export function AssetFeedGrid({
  assets,
  rowHeight = DEFAULT_ROW_HEIGHT,
  colWidth = DEFAULT_COL_WIDTH,
  onView,
}: Props) {
  const columns = useMemo(() => unionFeedColumns(assets), [assets]);
  // `useState` (not useRef) so virtualizers re-evaluate when the element mounts; ref
  // mutations don't trigger re-render.
  const [scrollEl, setScrollEl] = useState<HTMLDivElement | null>(null);
  // Tracks the body's horizontal scroll offset so the header row and pinned asset-name
  // column can transform in sync. `virtualizer.scrollOffset` isn't reactive on render.
  const [scrollLeft, setScrollLeft] = useState(0);

  // Lets the virtualizer render its first frame before ResizeObserver delivers a real
  // measurement. Load-bearing in jsdom where layout is not polyfilled.
  const initialRect = { width: 1200, height: 800 };

  // Row virtualization tracks the window — the grid lives in document flow and the page
  // scrolls vertically. Without this, rowVirt's scroll container height equals its
  // content height (no scroll → no row mounting beyond the initial window).
  const rowVirt = useVirtualizer({
    count: assets.length,
    getScrollElement: () =>
      typeof document !== "undefined"
        ? (document.scrollingElement as HTMLElement | null) ?? document.documentElement
        : null,
    estimateSize: () => rowHeight,
    overscan: 8,
    initialRect,
  });
  const colVirt = useVirtualizer({
    count: columns.length,
    getScrollElement: () => scrollEl,
    estimateSize: () => colWidth,
    horizontal: true,
    overscan: 4,
    initialRect,
  });

  const totalRowSize = rowVirt.getTotalSize();
  const totalColSize = colVirt.getTotalSize();

  return (
    <div className="flex flex-col">
      <div
        className="flex border-b border-border-subtle bg-bg-surface"
        style={{ height: rowHeight }}
      >
        <div
          className="shrink-0 border-r border-border-subtle px-2 flex items-center text-text-muted text-xs"
          style={{ width: ROW_HEADER_WIDTH }}
        >
          asset \ feed
        </div>
        <div className="relative overflow-hidden flex-1" style={{ height: rowHeight }}>
          {/* Inner wrapper translates inversely with body scrollLeft so headers track
              the cells beneath. */}
          <div style={{ transform: `translateX(${-scrollLeft}px)`, willChange: "transform" }}>
            {colVirt.getVirtualItems().map((c) => (
              <div
                key={c.key}
                style={{
                  position: "absolute",
                  left: c.start,
                  width: c.size,
                  height: rowHeight,
                }}
                className="px-2 flex items-center text-text-secondary text-xs font-mono truncate"
                title={columns[c.index].id}
              >
                {columns[c.index].id}
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* Body: column-axis virtualized in a horizontally-scrolling container; rows live
          in document flow so the page scrolls vertically. */}
      <div
        ref={setScrollEl}
        onScroll={(e) => setScrollLeft(e.currentTarget.scrollLeft)}
        className="overflow-x-auto"
      >
        <div
          style={{
            height: totalRowSize,
            width: ROW_HEADER_WIDTH + totalColSize,
            position: "relative",
          }}
        >
          {rowVirt.getVirtualItems().map((r) => {
            const asset = assets[r.index];
            return (
              <div key={r.key}>
                {/* Forward-translate by scrollLeft to pin the asset-name cell to the
                    viewport's left edge while the body scrolls right. */}
                <div
                  style={{
                    position: "absolute",
                    top: r.start,
                    left: 0,
                    width: ROW_HEADER_WIDTH,
                    height: r.size,
                    transform: `translateX(${scrollLeft}px)`,
                    willChange: "transform",
                    zIndex: 1,
                  }}
                  className="border-r border-border-subtle px-2 flex items-center bg-bg-surface text-sm font-mono text-text-primary truncate"
                  title={asset.display_name}
                >
                  {asset.display_name}
                </div>

                {colVirt.getVirtualItems().map((c) => (
                  <FeedCell
                    key={`${r.key}-${c.key}`}
                    asset={asset}
                    feedColumn={columns[c.index]}
                    onView={onView}
                    style={{
                      top: r.start,
                      left: ROW_HEADER_WIDTH + c.start,
                      width: c.size,
                      height: r.size,
                    }}
                  />
                ))}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
