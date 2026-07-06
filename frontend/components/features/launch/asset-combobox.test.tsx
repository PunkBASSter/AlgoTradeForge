import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AssetCombobox } from "./asset-combobox";
import type { AssetCatalogEntry } from "@/types/data-tab";

const fetchMock = vi.fn();
beforeEach(() => {
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  fetchMock.mockReset();
});

const entry = (exchange: string, symbol: string, type: string): AssetCatalogEntry => ({
  exchange, symbol, display_name: symbol, type, feeds: [],
});

function mockAssets(assets: AssetCatalogEntry[]) {
  fetchMock.mockResolvedValue(new Response(JSON.stringify({ assets }), { status: 200 }));
}

function renderCombobox(onSelect = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <AssetCombobox value={null} onSelect={onSelect} />
    </QueryClientProvider>,
  );
  return onSelect;
}

describe("AssetCombobox", () => {
  it("filters the catalog by the typed query across symbol and exchange", async () => {
    mockAssets([
      entry("NASDAQ", "AAPL", "equity"),
      entry("NASDAQ", "MSFT", "equity"),
      entry("binance", "BTCUSDT", "spot"),
    ]);
    renderCombobox();

    const input = await screen.findByRole("combobox", { name: /asset/i });
    fireEvent.change(input, { target: { value: "aapl" } });

    await waitFor(() => expect(screen.getByText("AAPL")).toBeInTheDocument());
    expect(screen.queryByText("BTCUSDT")).not.toBeInTheDocument();
  });

  it("emits the chosen entry on click", async () => {
    mockAssets([entry("binance", "BTCUSDT", "spot")]);
    const onSelect = renderCombobox();

    const input = await screen.findByRole("combobox", { name: /asset/i });
    fireEvent.change(input, { target: { value: "btc" } });
    fireEvent.click(await screen.findByText("BTCUSDT"));

    expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({ exchange: "binance", symbol: "BTCUSDT" }),
    );
  });

  it("shows a truncation hint when matches exceed the cap instead of silently dropping them", async () => {
    mockAssets(Array.from({ length: 60 }, (_, i) => entry("NASDAQ", `SYM${i}`, "equity")));
    renderCombobox();

    const input = await screen.findByRole("combobox", { name: /asset/i });
    fireEvent.focus(input);

    expect(await screen.findByText(/showing 50 of 60/i)).toBeInTheDocument();
  });

  it("surfaces an exact ticker above the cap via relevance ranking", async () => {
    // 55 substring matches ("SPY0".."SPY54") precede the exact "SPY" in catalog order —
    // without ranking the exact match sorts past the 50-row slice and vanishes.
    const substrings = Array.from({ length: 55 }, (_, i) => entry("ARCA", `SPY${i}`, "equity"));
    mockAssets([...substrings, entry("ARCA", "SPY", "equity")]);
    renderCombobox();

    const input = await screen.findByRole("combobox", { name: /asset/i });
    fireEvent.change(input, { target: { value: "spy" } });

    expect(await screen.findByText("SPY")).toBeInTheDocument();
  });
});
