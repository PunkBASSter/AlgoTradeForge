import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { FeedPicker, type FeedPickerSelection } from "./feed-picker";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

const fetchMock = vi.fn();
beforeEach(() => {
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  fetchMock.mockReset();
});

const timeBar: FeedCatalogEntry = {
  id: "1d",
  kind: "OHLCV_TimeBar",
  interval: "1d",
  type_code: null,
  threshold_value: null,
  sidecar: null,
};

function mockAssets(assets: AssetCatalogEntry[]) {
  fetchMock.mockResolvedValue(new Response(JSON.stringify({ assets }), { status: 200 }));
}

function renderPicker(value: FeedPickerSelection) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <FeedPicker role="Primary" value={value} onChange={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("FeedPicker", () => {
  it("hydrates the feed list from a prefilled value, with no onSelect fired", async () => {
    mockAssets([
      { exchange: "NASDAQ", symbol: "AAPL", display_name: "AAPL", type: "equity", feeds: [timeBar] },
    ]);

    // value arrives pre-populated (clone / restore / remount) — the picker must derive the
    // asset from it, not wait for the combobox's onSelect.
    renderPicker({ exchange: "NASDAQ", asset: "AAPL", feedId: "", subscription: null });

    expect(await screen.findByRole("option", { name: "1d" })).toBeInTheDocument();
    expect(
      screen.queryByRole("option", { name: /pick an asset first/i }),
    ).not.toBeInTheDocument();
  });
});
