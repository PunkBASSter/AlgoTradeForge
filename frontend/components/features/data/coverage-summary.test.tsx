import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CoverageSummary } from "./coverage-summary";
import type { AssetCatalogEntry, LoadRequestBody } from "@/types/data-tab";

// Hoisted so FakeDataApiError is available when the mock factory runs.
const { getCoverageSpy, postLoadSpy, FakeDataApiError } = vi.hoisted(() => {
  class FakeDataApiError extends Error {
    constructor(
      public status: number,
      public code: string | undefined,
      message: string,
      public body: unknown = null,
    ) {
      super(message);
    }
  }
  return { getCoverageSpy: vi.fn(), postLoadSpy: vi.fn(), FakeDataApiError };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    getCoverage: (...args: unknown[]) => getCoverageSpy(...args),
    postLoad: (...args: unknown[]) => postLoadSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

// Epoch ms helpers (UTC).
const JAN_2024 = Date.UTC(2024, 0, 1);   // 2024-01-01
const MAR_2024 = Date.UTC(2024, 2, 31);  // 2024-03-31

const ASSET: AssetCatalogEntry = {
  exchange: "binance",
  symbol: "BTCUSDT_perp",
  display_name: "BTCUSDT-perp",
  type: "perpetual",
  feeds: [],
};

const CANDLES_MAPPING = { feedName: "candles", interval: "1h" };

function renderSummary(
  mapping = CANDLES_MAPPING,
  asset: AssetCatalogEntry = ASSET,
) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <CoverageSummary exchange="binance" asset={asset} mapping={mapping} />
    </QueryClientProvider>,
  );
}

function makeCoverageResponse(partial: {
  covered_months: string[];
  first_timestamp: number | null;
  last_timestamp: number | null;
}) {
  return {
    asset_dir: "BTCUSDT_perp",
    feeds: [
      {
        feed_name: "candles",
        interval: "1h",
        covered_months: partial.covered_months,
        first_timestamp: partial.first_timestamp,
        last_timestamp: partial.last_timestamp,
      },
    ],
  };
}

beforeEach(() => {
  getCoverageSpy.mockReset();
  postLoadSpy.mockReset();
});

describe("CoverageSummary", () => {
  describe("fully covered — no banner", () => {
    it("renders month count and range; no alert banner", async () => {
      getCoverageSpy.mockResolvedValue(
        makeCoverageResponse({
          covered_months: ["2024-01", "2024-02", "2024-03"],
          first_timestamp: JAN_2024,
          last_timestamp: MAR_2024,
        }),
      );

      renderSummary();

      await waitFor(() =>
        expect(screen.getByText(/Archive coverage: 3 months/)).toBeInTheDocument(),
      );
      expect(screen.getByText(/2024-01 – 2024-03/)).toBeInTheDocument();
      expect(screen.queryByRole("alert")).toBeNull();
    });
  });

  describe("hole inside known window — banner + load button", () => {
    it("lists the missing month and clicking Load posts the exact range", async () => {
      getCoverageSpy.mockResolvedValue(
        makeCoverageResponse({
          covered_months: ["2024-01", "2024-03"],
          first_timestamp: JAN_2024,
          last_timestamp: MAR_2024,
        }),
      );
      postLoadSpy.mockResolvedValue({ job_id: "test-job-abc123" });

      renderSummary();

      // Banner must list the missing month.
      const banner = await screen.findByRole("alert");
      expect(banner).toHaveTextContent("2024-02");

      // Button must be present.
      const btn = screen.getByRole("button", { name: /load missing months/i });
      expect(btn).toBeInTheDocument();

      // Regression pin: getCoverage must use the EXCHANGE symbol (BTCUSDT), not display_name (BTCUSDT-perp).
      await waitFor(() => expect(getCoverageSpy).toHaveBeenCalled());
      expect(getCoverageSpy).toHaveBeenCalledWith(
        "binance",
        "BTCUSDT",
        "perpetual",
        expect.anything(),
      );

      fireEvent.click(btn);

      await waitFor(() => expect(postLoadSpy).toHaveBeenCalledOnce());

      const [body] = postLoadSpy.mock.calls[0] as [LoadRequestBody, ...unknown[]];
      // Regression pin: postLoad symbol must be the EXCHANGE symbol (BTCUSDT), not display_name (BTCUSDT-perp).
      expect(body).toEqual({
        exchange: "binance",
        symbol: "BTCUSDT",
        asset_type: "perpetual",
        feed_name: "candles",
        interval: "1h",
        from: "2024-02-01",
        to: "2024-02-29", // 2024 is a leap year
      });
    });
  });

  describe("first_timestamp null — count only, no banner", () => {
    it("renders count without range and without alert", async () => {
      getCoverageSpy.mockResolvedValue(
        makeCoverageResponse({
          covered_months: ["2024-01"],
          first_timestamp: null,
          last_timestamp: null,
        }),
      );

      renderSummary();

      await waitFor(() =>
        expect(screen.getByText(/Archive coverage: 1 month/)).toBeInTheDocument(),
      );
      // No range in parens since timestamps are absent.
      expect(screen.queryByText(/2024-01 –/)).toBeNull();
      expect(screen.queryByRole("alert")).toBeNull();
    });
  });

  describe("no matching coverage entry — renders nothing", () => {
    it("returns null when the coverage feed list does not match the mapping", async () => {
      getCoverageSpy.mockResolvedValue({ asset_dir: "BTCUSDT_perp", feeds: [] });

      const { container } = renderSummary();

      // Poll until loading clears and the component settles to an empty render.
      await waitFor(() => expect(container.firstChild).toBeNull());
    });
  });
});
