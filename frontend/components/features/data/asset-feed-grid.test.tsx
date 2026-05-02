import { describe, it, expect, beforeEach } from "vitest";
import { render, fireEvent } from "@testing-library/react";
import { AssetFeedGrid } from "./asset-feed-grid";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

// jsdom doesn't implement layout — useVirtualizer reads scrollElement.clientHeight /
// clientWidth via ResizeObserver. We stub these so the virtualizer knows our viewport
// size deterministically.
beforeEach(() => {
  // jsdom defaults all layout properties to 0. TanStack Virtual reads `offsetWidth` /
  // `offsetHeight` via `getRect()` for initial measurement — without stubs, the
  // virtualizer would set scrollRect to {0,0} and render nothing.
  Object.defineProperty(HTMLElement.prototype, "clientHeight", { configurable: true, get: () => 480 });
  Object.defineProperty(HTMLElement.prototype, "clientWidth",  { configurable: true, get: () => 800 });
  Object.defineProperty(HTMLElement.prototype, "scrollHeight", { configurable: true, get: () => 480 });
  Object.defineProperty(HTMLElement.prototype, "scrollWidth",  { configurable: true, get: () => 800 });
  Object.defineProperty(HTMLElement.prototype, "offsetWidth",  { configurable: true, get: () => 800 });
  Object.defineProperty(HTMLElement.prototype, "offsetHeight", { configurable: true, get: () => 480 });
  Object.defineProperty(HTMLElement.prototype, "getBoundingClientRect", {
    configurable: true,
    value() {
      return { top: 0, left: 0, right: 800, bottom: 480, width: 800, height: 480, x: 0, y: 0, toJSON() { } };
    },
  });

  // jsdom lacks ResizeObserver. The polyfill must invoke its callback with the initial
  // size synchronously after `observe()` so useVirtualizer's measurement pass runs.
  (globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
    private callback: ResizeObserverCallback;
    constructor(cb: ResizeObserverCallback) { this.callback = cb; }
    observe(target: Element): void {
      this.callback(
        [{
          target,
          contentRect: { top: 0, left: 0, right: 800, bottom: 480, width: 800, height: 480, x: 0, y: 0 },
          borderBoxSize: [], contentBoxSize: [], devicePixelContentBoxSize: [],
        } as unknown as ResizeObserverEntry],
        this as unknown as ResizeObserver,
      );
    }
    unobserve(): void {}
    disconnect(): void {}
  };
});

const tb = (id: string, interval: string): FeedCatalogEntry => ({
  id, kind: "OHLCV_TimeBar", interval, type_code: null, threshold_value: null, sidecar: null,
});
const alt = (id: string, type: string, threshold: number, sidecar: string | null = null): FeedCatalogEntry => ({
  id, kind: "OHLCV_AltBar", interval: null, type_code: type, threshold_value: threshold, sidecar,
});

function makeAssets(rowCount: number, feedsPerAsset: FeedCatalogEntry[]): AssetCatalogEntry[] {
  return Array.from({ length: rowCount }, (_, i) => ({
    exchange: "binance",
    asset: `ASSET${i.toString().padStart(4, "0")}`,
    asset_class: "crypto-perp",
    type: "CryptoPerpetual",
    feeds: feedsPerAsset,
  }));
}

describe("AssetFeedGrid", () => {
  it("renders only viewport cells for a 10k-cell grid (P3-13)", () => {
    // 500 assets × 20 feeds = 10,000 logical cells. With 480x800 viewport, 36px rows
    // and 132px cols, the visible window is ~13 rows × 6 cols = 78 cells. With overscan
    // of 8 rows + 4 cols, that's ~(13+16) × (6+8) ≈ ~400 cells worst-case. We assert
    // the DOM holds ≤500 cells (P3-13: ≪10000).
    const feeds: FeedCatalogEntry[] = [
      tb("1m", "1m"), tb("5m", "5m"), tb("15m", "15m"), tb("1h", "1h"), tb("4h", "4h"),
      alt("EqV_1m_1000", "EqV", 1000), alt("EqV_1m_5000", "EqV", 5000),
      alt("EqV_5m_1000", "EqV", 1000), alt("EqT_1m_500", "EqT", 500),
      alt("EqI_ticks_500", "EqI", 500, "EqI_ticks_500.flow"),
      alt("EqD_1m_100k", "EqD", 100000), alt("EqD_5m_100k", "EqD", 100000),
      alt("EqV_15m_5000", "EqV", 5000), alt("EqV_1h_5000", "EqV", 5000),
      alt("EqT_5m_500", "EqT", 500), alt("EqT_15m_500", "EqT", 500),
      alt("EqV_4h_5000", "EqV", 5000), alt("EqV_1d_5000", "EqV", 5000),
      alt("EqT_1h_500", "EqT", 500), alt("EqT_4h_500", "EqT", 500),
    ];
    expect(feeds.length).toBe(20);
    const assets = makeAssets(500, feeds);

    const { container } = render(<AssetFeedGrid assets={assets} />);

    // Cell affordance: every body cell is a <button>. We exclude the asset-name divs
    // (no role) and the header label divs.
    const cellButtons = container.querySelectorAll("button");
    expect(cellButtons.length).toBeLessThan(500);
    expect(cellButtons.length).toBeGreaterThan(0);

    // Sanity: with 10k logical cells and ≤500 in the DOM, virtualization reduces by 20×.
    expect(cellButtons.length / (500 * 20)).toBeLessThan(0.05);
  });

  it("renders sidecar indicator dot for EqI feeds with non-null sidecar (P3-14)", () => {
    const feeds: FeedCatalogEntry[] = [
      tb("1m", "1m"),
      alt("EqI_ticks_500", "EqI", 500, "EqI_ticks_500.flow"),
    ];
    const assets = makeAssets(2, feeds);

    const { container } = render(<AssetFeedGrid assets={assets} />);

    // The sidecar dot has aria-label="has sidecar".
    const dots = container.querySelectorAll('[aria-label="has sidecar"]');
    expect(dots.length).toBe(2);   // one per asset row, only on the EqI column
  });

  it("does not crash on an empty asset list", () => {
    const { container } = render(<AssetFeedGrid assets={[]} />);
    expect(container).toBeTruthy();
  });

  it("translates header columns and pins asset-name cells when body scrolls horizontally (P3-12 follow-up)", () => {
    // Regression test for the bug where the header row (rendered outside the body's
    // scroll container) drifted away from the cells beneath it on horizontal scroll,
    // and the asset-name column scrolled off-screen left when the body scrolled right.
    // The fix: track scrollLeft in state via onScroll, then translateX(-scrollLeft) on
    // the header inner wrapper and translateX(+scrollLeft) on each asset-name cell.
    const feeds: FeedCatalogEntry[] = [
      tb("1m", "1m"), tb("5m", "5m"), tb("15m", "15m"), tb("1h", "1h"),
      alt("EqV_1m_1000", "EqV", 1000), alt("EqV_1m_5000", "EqV", 5000),
      alt("EqV_5m_1000", "EqV", 1000), alt("EqV_5m_5000", "EqV", 5000),
      alt("EqT_1m_500", "EqT", 500), alt("EqT_5m_500", "EqT", 500),
    ];
    const assets = makeAssets(5, feeds);

    const { container } = render(<AssetFeedGrid assets={assets} />);

    // The body scroll container is the only div with overflow-auto.
    const scrollEl = container.querySelector(".overflow-auto") as HTMLDivElement;
    expect(scrollEl).toBeTruthy();

    // Header inner wrapper: the div with translateX style sits inside the
    // overflow-hidden flex-1 div in the header row. Locate it via its inline
    // transform attribute (set on initial render to "translateX(0px)").
    const allWithTransform = Array.from(
      container.querySelectorAll('div[style*="translateX"]'),
    ) as HTMLDivElement[];
    // Pick the header inner wrapper specifically: it has no `title` attribute and is the
    // first transform-bearing div in document order.
    const headerInner = allWithTransform.find((el) => !el.hasAttribute("title"));
    expect(headerInner).toBeTruthy();
    expect(headerInner!.style.transform).toBe("translateX(0px)");

    // Asset-name cells carry title=ASSET<n>. Sample row 0.
    const assetCell = container.querySelector('[title="ASSET0000"]') as HTMLDivElement;
    expect(assetCell).toBeTruthy();
    expect(assetCell.style.transform).toBe("translateX(0px)");

    // Simulate horizontal scroll. jsdom doesn't run scroll physics; we set scrollLeft
    // directly on the element, then dispatch the scroll event so React's onScroll fires
    // and reads currentTarget.scrollLeft.
    Object.defineProperty(scrollEl, "scrollLeft", {
      configurable: true,
      value: 250,
      writable: true,
    });
    fireEvent.scroll(scrollEl);

    // Header inner wrapper translates LEFT by 250px (columns visually follow the body).
    expect(headerInner!.style.transform).toBe("translateX(-250px)");

    // Asset-name cell translates RIGHT by 250px (stays pinned at viewport's left edge).
    expect(assetCell.style.transform).toBe("translateX(250px)");
  });

  it("orders columns: time bars → alt bars (by type/threshold) → ticks → side feeds (P3-12)", () => {
    const feeds: FeedCatalogEntry[] = [
      { id: "funding-rate", kind: "Side", interval: null, type_code: null, threshold_value: null, sidecar: null },
      { id: "ticks", kind: "Tick", interval: null, type_code: null, threshold_value: null, sidecar: null },
      alt("EqV_1m_5000", "EqV", 5000),
      alt("EqI_ticks_500", "EqI", 500),
      tb("5m", "5m"),
      tb("1m", "1m"),
      alt("EqV_1m_1000", "EqV", 1000),
    ];
    const assets = makeAssets(1, feeds);

    const { container } = render(<AssetFeedGrid assets={assets} />);

    // Header column labels render in document order; capture by `title=` (set on the truncate div).
    const labels = Array.from(container.querySelectorAll("[title]"))
      .map((el) => el.getAttribute("title"))
      .filter((t): t is string => !!t)
      .filter((t) => t !== "ASSET0000");   // exclude the asset-name cell's title

    expect(labels).toEqual([
      "1m", "5m",
      "EqI_ticks_500", "EqV_1m_1000", "EqV_1m_5000",
      "ticks",
      "funding-rate",
    ]);
  });
});
