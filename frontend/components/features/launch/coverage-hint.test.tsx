import React from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CoverageHint } from "./coverage-hint";
import type { DataFeedSubscription } from "@/types/api";
import type { LoadRequestBody } from "@/types/data-tab";

// Hoisted so FakeDataApiError is available when the vi.mock factory runs.
const { getAssetsSpy, getCoverageSpy, postLoadSpy, getLoadJobSpy, FakeDataApiError, toastSpy } =
  vi.hoisted(() => {
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
    return {
      getAssetsSpy: vi.fn(),
      getCoverageSpy: vi.fn(),
      postLoadSpy: vi.fn(),
      getLoadJobSpy: vi.fn(),
      toastSpy: vi.fn(),
      FakeDataApiError,
    };
  });

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    getAssets: (...args: unknown[]) => getAssetsSpy(...args),
    getCoverage: (...args: unknown[]) => getCoverageSpy(...args),
    postLoad: (...args: unknown[]) => postLoadSpy(...args),
    getLoadJob: (...args: unknown[]) => getLoadJobSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastSpy }),
}));

const ASSET = {
  exchange: "binance",
  symbol: "BTCUSDT_perp",
  display_name: "BTCUSDT-perp",
  type: "perpetual",
  feeds: [],
};

const TIME_BAR_PRIMARY: DataFeedSubscription = {
  kind: "TimeBar",
  role: "Primary",
  assetName: "BTCUSDT_perp",
  exchange: "binance",
  timeFrame: "1h",
};

// A terminal job snapshot returned by getLoadJob so polling stops immediately.
const TERMINAL_JOB = {
  job_id: "job-abc-123",
  state: "complete" as const,
  months_done: 1,
  months_total: 1,
  current_month: null,
  error_code: null,
  error_message: null,
  symbol: "BTCUSDT",
  feed_name: "candles",
  interval: "1h",
  from: "2024-02-01",
  to: "2024-02-29",
  queued_at: "",
  completed_at: null,
};

beforeEach(() => {
  getAssetsSpy.mockReset();
  getCoverageSpy.mockReset();
  postLoadSpy.mockReset();
  getLoadJobSpy.mockReset();
  toastSpy.mockReset();
  // Default: terminal state so refetchInterval stops after first poll.
  getLoadJobSpy.mockResolvedValue(TERMINAL_JOB);
});

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe("CoverageHint", () => {
  it("renders nothing when all months in the range are covered", async () => {
    getAssetsSpy.mockResolvedValue({ assets: [ASSET] });
    getCoverageSpy.mockResolvedValue({
      asset_dir: "BTCUSDT_perp",
      feeds: [
        {
          feed_name: "candles",
          interval: "1h",
          covered_months: ["2024-01", "2024-02", "2024-03"],
          first_timestamp: null,
          last_timestamp: null,
        },
      ],
    });

    wrap(
      <CoverageHint
        primaries={[TIME_BAR_PRIMARY]}
        startTime="2024-01-01"
        endTime="2024-03-31"
      />,
    );

    await waitFor(() => expect(getCoverageSpy).toHaveBeenCalled());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("renders a banner with count and interval when months are missing; Load posts the exact body", async () => {
    getAssetsSpy.mockResolvedValue({ assets: [ASSET] });
    getCoverageSpy.mockResolvedValue({
      asset_dir: "BTCUSDT_perp",
      feeds: [
        {
          feed_name: "candles",
          interval: "1h",
          covered_months: ["2024-01", "2024-03"],
          first_timestamp: null,
          last_timestamp: null,
        },
      ],
    });
    postLoadSpy.mockResolvedValue({ job_id: "job-abc-123" });

    wrap(
      <CoverageHint
        primaries={[TIME_BAR_PRIMARY]}
        startTime="2024-01-01"
        endTime="2024-03-31"
      />,
    );

    const banner = await screen.findByRole("alert");
    // Banner must mention the interval and the count.
    expect(banner).toHaveTextContent("1h");
    expect(banner).toHaveTextContent("1 archived month");

    // Regression pin: getCoverage must use the EXCHANGE symbol (BTCUSDT), not display_name (BTCUSDT-perp).
    expect(getCoverageSpy).toHaveBeenCalledWith(
      "binance",
      "BTCUSDT",
      "perpetual",
      expect.anything(),
    );

    fireEvent.click(screen.getByRole("button", { name: /load/i }));

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

  it("renders nothing (no crash) when the subscription asset is absent from the catalog", async () => {
    getAssetsSpy.mockResolvedValue({ assets: [] });

    wrap(
      <CoverageHint
        primaries={[TIME_BAR_PRIMARY]}
        startTime="2024-01-01"
        endTime="2024-03-31"
      />,
    );

    await waitFor(() => expect(getAssetsSpy).toHaveBeenCalled());
    expect(screen.queryByRole("alert")).toBeNull();
    expect(getCoverageSpy).not.toHaveBeenCalled();
  });

  it("clamps range start to first_timestamp — months before the listing date are not flagged", async () => {
    getAssetsSpy.mockResolvedValue({ assets: [ASSET] });
    // first_timestamp = 2024-03-01 epoch ms; so 2024-01 and 2024-02 must NOT be flagged.
    const march1stMs = new Date("2024-03-01T00:00:00Z").getTime();
    getCoverageSpy.mockResolvedValue({
      asset_dir: "BTCUSDT_perp",
      feeds: [
        {
          feed_name: "candles",
          interval: "1h",
          covered_months: [],
          first_timestamp: march1stMs,
          last_timestamp: null,
        },
      ],
    });

    wrap(
      <CoverageHint
        primaries={[TIME_BAR_PRIMARY]}
        startTime="2024-01-01"
        endTime="2024-05-31"
      />,
    );

    const banner = await screen.findByRole("alert");
    expect(banner).not.toHaveTextContent("2024-01");
    expect(banner).not.toHaveTextContent("2024-02");
    expect(banner).toHaveTextContent("2024-03");
  });

  it("null first_timestamp — all uncovered closed months in range are flagged unchanged", async () => {
    getAssetsSpy.mockResolvedValue({ assets: [ASSET] });
    getCoverageSpy.mockResolvedValue({
      asset_dir: "BTCUSDT_perp",
      feeds: [
        {
          feed_name: "candles",
          interval: "1h",
          covered_months: [],
          first_timestamp: null,
          last_timestamp: null,
        },
      ],
    });

    wrap(
      <CoverageHint
        primaries={[TIME_BAR_PRIMARY]}
        startTime="2024-01-01"
        endTime="2024-03-31"
      />,
    );

    const banner = await screen.findByRole("alert");
    expect(banner).toHaveTextContent("3 archived months");
  });

  it("non-409 error from postLoad — fires error toast and does not start polling", async () => {
    getAssetsSpy.mockResolvedValue({ assets: [ASSET] });
    getCoverageSpy.mockResolvedValue({
      asset_dir: "BTCUSDT_perp",
      feeds: [
        {
          feed_name: "candles",
          interval: "1h",
          covered_months: [],
          first_timestamp: null,
          last_timestamp: null,
        },
      ],
    });
    postLoadSpy.mockRejectedValueOnce(
      new FakeDataApiError(422, "not_replenishable", "Feed not replenishable"),
    );

    wrap(
      <CoverageHint
        primaries={[TIME_BAR_PRIMARY]}
        startTime="2024-01-01"
        endTime="2024-03-31"
      />,
    );

    await screen.findByRole("alert");
    fireEvent.click(screen.getByRole("button", { name: /load/i }));

    await waitFor(() => expect(toastSpy).toHaveBeenCalledOnce());
    expect(toastSpy).toHaveBeenCalledWith("Feed not replenishable", "error");
    // jobId was never set so getLoadJob is never called (no polling started).
    expect(getLoadJobSpy).not.toHaveBeenCalled();
  });

  it("ignores non-TimeBar primaries and renders nothing", () => {
    const altBar: DataFeedSubscription = {
      kind: "AltBar",
      role: "Primary",
      assetName: "BTCUSDT_perp",
      exchange: "binance",
      feedId: "EqV-500k",
    };
    const tick: DataFeedSubscription = {
      kind: "Tick",
      role: "Primary",
      assetName: "BTCUSDT_perp",
      exchange: "binance",
    };

    wrap(
      <CoverageHint
        primaries={[altBar, tick]}
        startTime="2024-01-01"
        endTime="2024-03-31"
      />,
    );

    // CoverageHint returns null before any query fires when no TimeBar primaries exist.
    expect(getAssetsSpy).not.toHaveBeenCalled();
    expect(getCoverageSpy).not.toHaveBeenCalled();
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
