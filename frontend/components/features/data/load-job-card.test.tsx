import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { LoadJobCard } from "./load-job-card";

// vi.mock factories are hoisted above top-level declarations; FakeDataApiError must live
// inside vi.hoisted() so it is available when the factory runs.
const { getLoadJobSpy, FakeDataApiError } = vi.hoisted(() => {
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
  return { getLoadJobSpy: vi.fn(), FakeDataApiError };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    getLoadJob: (...args: unknown[]) => getLoadJobSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

const RUNNING_SNAPSHOT = {
  job_id: "j1",
  state: "running" as const,
  queued_at: "2024-01-01T00:00:00Z",
  completed_at: null,
  months_done: 2,
  months_total: 5,
  current_month: "2024-03",
  error_code: null,
  error_message: null,
  symbol: "BTCUSDT",
  feed_name: "candles",
  interval: "1h",
  from: "2024-01-01",
  to: "2024-05-31",
};

const COMPLETE_SNAPSHOT = {
  ...RUNNING_SNAPSHOT,
  state: "complete" as const,
  completed_at: "2024-05-31T23:59:59Z",
  months_done: 5,
  current_month: null,
};

beforeEach(() => {
  getLoadJobSpy.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

function renderCard(jobId = "j1") {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <LoadJobCard jobId={jobId} onDismiss={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("LoadJobCard", () => {
  it("renders symbol, feed_name, interval, months_done/months_total, and current_month", async () => {
    getLoadJobSpy.mockResolvedValue(RUNNING_SNAPSHOT);
    renderCard();

    await waitFor(() => screen.getByText(/BTCUSDT candles 1h/));
    expect(screen.getByText(/2\/5 months/)).toBeInTheDocument();
    expect(screen.getByText(/2024-03/)).toBeInTheDocument();
  });

  it("terminal state stops polling — call count stabilizes after 10s", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    getLoadJobSpy.mockResolvedValue(COMPLETE_SNAPSHOT);
    renderCard();

    // Wait for first successful fetch (terminal state).
    await waitFor(() => expect(getLoadJobSpy).toHaveBeenCalled());
    const countAfterFirst = getLoadJobSpy.mock.calls.length;

    // Advance well past the 2s polling interval — no further fetches expected.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });

    expect(getLoadJobSpy.mock.calls.length).toBe(countAfterFirst);
  });

  it("404 response auto-removes the card (onDismiss called) and stops polling", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    getLoadJobSpy.mockRejectedValue(
      new FakeDataApiError(404, "job_not_found", "404 Not Found"),
    );
    const onDismiss = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <LoadJobCard jobId="j1" onDismiss={onDismiss} />
      </QueryClientProvider>,
    );

    // Auto-dismiss fires when 404 is detected (removeJob equivalent).
    await waitFor(() => expect(onDismiss).toHaveBeenCalledOnce());

    // Polling also stops after the 404.
    const countAfterFirst = getLoadJobSpy.mock.calls.length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(getLoadJobSpy.mock.calls.length).toBe(countAfterFirst);
  });

  it("renders error_message in a banner for failed jobs", async () => {
    getLoadJobSpy.mockResolvedValue({
      ...COMPLETE_SNAPSHOT,
      state: "error",
      error_message: "Download failed: 503 Service Unavailable",
    });
    renderCard();

    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        "Download failed: 503 Service Unavailable",
      ),
    );
  });

  it("dismiss button is rendered and calls onDismiss", async () => {
    getLoadJobSpy.mockResolvedValue(COMPLETE_SNAPSHOT);
    const onDismiss = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <LoadJobCard jobId="j1" onDismiss={onDismiss} />
      </QueryClientProvider>,
    );

    const btn = await screen.findByRole("button", { name: /dismiss/i });
    btn.click();
    expect(onDismiss).toHaveBeenCalledOnce();
  });
});
