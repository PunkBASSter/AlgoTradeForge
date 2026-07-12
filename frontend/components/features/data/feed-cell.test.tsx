import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { FeedCell } from "./feed-cell";
import type { AssetCatalogEntry, FeedCatalogEntry } from "@/types/data-tab";

// vi.hoisted so the mock factory can reference FakeDataApiError before it's used.
const { postMaterializeSpy, FakeDataApiError } = vi.hoisted(() => {
  class FakeDataApiError extends Error {
    constructor(
      public status: number,
      public code: string | undefined,
      message: string,
      public body: unknown = null,
    ) {
      super(message);
      this.name = "DataApiError";
    }
  }
  return { postMaterializeSpy: vi.fn(), FakeDataApiError };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    postMaterialize: (...args: unknown[]) => postMaterializeSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

const toastSpy = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastSpy }),
}));

vi.mock("@/lib/data/coverage-mapping", () => ({
  exchangeSymbolOf: (asset: { symbol: string }) =>
    asset.symbol.endsWith("_perp") ? asset.symbol.slice(0, -"_perp".length) : asset.symbol,
}));

beforeEach(() => {
  postMaterializeSpy.mockReset();
  toastSpy.mockReset();
});

const absentFeedColumn: FeedCatalogEntry = {
  id: "klines_1h",
  kind: "OHLCV_TimeBar",
  interval: "1h",
  type_code: null,
  threshold_value: null,
  sidecar: null,
};

const assetNoFeeds: AssetCatalogEntry = {
  exchange: "binance",
  symbol: "BTCUSDT_perp",
  display_name: "BTCUSDT",
  type: "CryptoPerpetual",
  feeds: [],
};

const cellStyle = { position: "absolute" as const, top: 0, left: 0, width: 132, height: 36 };

function renderCell(
  asset: AssetCatalogEntry,
  feedColumn: FeedCatalogEntry,
  onView?: () => void,
) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={qc}>
      <FeedCell asset={asset} feedColumn={feedColumn} style={cellStyle} onView={onView} />
    </QueryClientProvider>,
  );
}

describe("FeedCell — Materialize button (absent feed)", () => {
  it("renders a Materialize button when the feed is absent", () => {
    postMaterializeSpy.mockResolvedValueOnce({ job_id: "mat-00000000", location: "/api/data/jobs/mat-00000000" });
    renderCell(assetNoFeeds, absentFeedColumn);
    expect(screen.getByRole("button", { name: /materialize/i })).toBeInTheDocument();
  });

  it("posts { exchange, symbol, feed } when clicked", async () => {
    postMaterializeSpy.mockResolvedValueOnce({ job_id: "mat-aabbccdd", location: "/api/data/jobs/mat-aabbccdd" });
    renderCell(assetNoFeeds, absentFeedColumn);

    fireEvent.click(screen.getByRole("button", { name: /materialize/i }));

    await waitFor(() => expect(postMaterializeSpy).toHaveBeenCalledOnce());
    expect(postMaterializeSpy).toHaveBeenCalledWith({
      exchange: "binance",
      symbol: "BTCUSDT_perp",
      feed: "klines_1h",
    });
  });

  it("shows a success toast with job id on 202", async () => {
    postMaterializeSpy.mockResolvedValueOnce({ job_id: "mat-aabbccdd", location: "/api/data/jobs/mat-aabbccdd" });
    renderCell(assetNoFeeds, absentFeedColumn);

    fireEvent.click(screen.getByRole("button", { name: /materialize/i }));

    await waitFor(() => expect(toastSpy).toHaveBeenCalledOnce());
    const [msg, variant] = toastSpy.mock.calls[0];
    expect(msg).toMatch(/klines_1h/);
    expect(msg).toMatch(/mat-aabb/);
    expect(variant).toBe("success");
  });

  it("409 feed_busy shows info toast with Jobs panel hint, NOT an error banner", async () => {
    postMaterializeSpy.mockRejectedValueOnce(
      new FakeDataApiError(409, "feed_busy", "409 Conflict", { active_job_id: "existing-job" }),
    );
    renderCell(assetNoFeeds, absentFeedColumn);

    fireEvent.click(screen.getByRole("button", { name: /materialize/i }));

    await waitFor(() => expect(toastSpy).toHaveBeenCalledOnce());
    const [msg, variant] = toastSpy.mock.calls[0];
    expect(msg).toMatch(/already materializ/i);
    expect(msg).toMatch(/Jobs panel/i);
    expect(variant).toBe("info");
    // No error banner rendered — the button is still visible (not replaced by an alert).
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("non-409 error shows error toast with the error message", async () => {
    postMaterializeSpy.mockRejectedValueOnce(
      new FakeDataApiError(422, "feed_not_materializable", "422 Unprocessable Entity"),
    );
    renderCell(assetNoFeeds, absentFeedColumn);

    fireEvent.click(screen.getByRole("button", { name: /materialize/i }));

    await waitFor(() => expect(toastSpy).toHaveBeenCalledOnce());
    const [, variant] = toastSpy.mock.calls[0];
    expect(variant).toBe("error");
  });
});

describe("FeedCell — present feed cells", () => {
  const presentFeed: FeedCatalogEntry = {
    id: "klines_1h",
    kind: "OHLCV_TimeBar",
    interval: "1h",
    type_code: null,
    threshold_value: null,
    sidecar: null,
  };
  const assetWithFeed: AssetCatalogEntry = {
    ...assetNoFeeds,
    feeds: [presentFeed],
  };

  it("renders a View button (not Materialize) when the feed is present", () => {
    renderCell(assetWithFeed, presentFeed);
    expect(screen.queryByRole("button", { name: /materialize/i })).toBeNull();
    expect(screen.getByRole("button", { name: /view/i })).toBeInTheDocument();
  });

  it("calls onView when a present feed cell is clicked", () => {
    const onView = vi.fn();
    renderCell(assetWithFeed, presentFeed, onView);
    fireEvent.click(screen.getByRole("button", { name: /view/i }));
    expect(onView).toHaveBeenCalledOnce();
    expect(onView).toHaveBeenCalledWith(assetWithFeed, presentFeed);
  });
});
