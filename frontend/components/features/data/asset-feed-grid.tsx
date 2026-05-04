"use client";

// Phase 3 — both-axis virtualized asset×feed grid (P3-12, P3-13). Two `useVirtualizer`
// instances share a single scroll container so scroll state is consistent. P3-13 caps
// DOM cell count: at 500 assets × 20 feeds (10k cells) only a small viewport-window of
// cells should mount.

import { useMemo, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";
import { unionFeedColumns } from "@/lib/data/feed-order";
import { FeedCell } from "./feed-cell";

interface Props {
  assets: AssetCatalogEntry[];
  height?: number;       // px; defaults to 480
  rowHeight?: number;    // px; defaults to 36
  colWidth?: number;     // px; defaults to 132
  onAdd?: (asset: AssetCatalogEntry, sourceFeed: FeedCatalogEntry) => void;
  onView?: (asset: AssetCatalogEntry, feed: FeedCatalogEntry) => void;
}

const DEFAULT_HEIGHT = 480;
const DEFAULT_ROW_HEIGHT = 36;
const DEFAULT_COL_WIDTH = 132;
const ROW_HEADER_WIDTH = 160;     // pixel width of the asset-name header column

export function AssetFeedGrid({
  assets,
  height = DEFAULT_HEIGHT,
  rowHeight = DEFAULT_ROW_HEIGHT,
  colWidth = DEFAULT_COL_WIDTH,
  onAdd,
  onView,
}: Props) {
  const columns = useMemo(() => unionFeedColumns(assets), [assets]);
  // `useState`-tracked scroll element (not useRef) so the virtualizers re-evaluate when
  // the element mounts. With useRef, ref.current changes don't trigger re-render and the
  // virtualizers never see the element on first commit.
  const [scrollEl, setScrollEl] = useState<HTMLDivElement | null>(null);
  // Native onScroll handler tracks the body's horizontal scroll offset so the header row
  // (rendered outside the scroll container) and the asset-name column (logically pinned)
  // can apply transforms. TanStack Virtual's internal observeElementOffset is non-public,
  // and `virtualizer.scrollOffset` isn't reactive on render — JS-driven transforms are
  // the canonical pattern for two-axis virtualization with a sticky row/column.
  const [scrollLeft, setScrollLeft] = useState(0);

  // `initialRect` lets the virtualizer render its first frame at the prop-supplied size
  // before ResizeObserver delivers a real measurement. In production this avoids a flash
  // of empty-grid; in jsdom (where ResizeObserver is polyfilled but layout is not), it's
  // load-bearing for tests to render any cells at all.
  const initialRect = { width: 1200, height };

  const rowVirt = useVirtualizer({
    count: assets.length,
    getScrollElement: () => scrollEl,
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

  // We don't virtualize the asset-name column separately — there's only one cell per row
  // and rowVirt already gates which rows mount. Stickiness comes from a per-cell
  // translateX(scrollLeft) further down (see the asset-name cell render).

  return (
    <div className="flex flex-col">
      {/* Header row — feed-id labels above each column. Renders the same virtual cols. */}
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
          {/* Inner wrapper translates inversely with the body's scrollLeft so the header
              columns visually track the cells beneath them. `willChange: transform` hints
              the browser to promote this layer for smooth scrolling. */}
          <div style={{ transform: `translateX(${-scrollLeft}px)`, willChange: "transform" }}>
            {/* Header columns: render the SAME virtual items as the body so they align. */}
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

      {/* Body — both axes virtualized. */}
      <div
        ref={setScrollEl}
        onScroll={(e) => setScrollLeft(e.currentTarget.scrollLeft)}
        className="overflow-auto"
        style={{ height }}
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
                {/* Asset-name cell: forward-translate by scrollLeft so it stays pinned to
                    the viewport's left edge as the body scrolls right. zIndex layers it
                    above the feed cells that pass beneath. bg-bg-surface (opaque) so feed
                    cells don't bleed through. */}
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
                    onAdd={onAdd}
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
