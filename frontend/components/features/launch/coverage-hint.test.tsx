import React from "react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CoverageHint } from "./coverage-hint";
import { useLoadJobsStore } from "@/lib/stores/load-jobs-store";
import type { DataFeedSubscription } from "@/types/api";
import type { LoadRequestBody } from "@/types/data-tab";

// Hoisted so FakeDataApiError is available when the vi.mock factory runs.
const { getAssetsSpy, getCoverageSpy, postLoadSpy, getLoadJobSpy, FakeDataApiError } =
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

const ASSET = {
  exchange: "binance",
  symbol: "BTCUSDT_perp",
  display_name: "BTCUSDT",
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
  // Default: terminal state so refetchInterval stops after first poll.
  getLoadJobSpy.mockResolvedValue(TERMINAL_JOB);
  useLoadJobsStore.setState({ jobs: {} });
});

afterEach(() => {
  useLoadJobsStore.setState({ jobs: {} });
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

    fireEvent.click(screen.getByRole("button", { name: /load/i }));

    await waitFor(() => expect(postLoadSpy).toHaveBeenCalledOnce());
    const [body] = postLoadSpy.mock.calls[0] as [LoadRequestBody, ...unknown[]];
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
