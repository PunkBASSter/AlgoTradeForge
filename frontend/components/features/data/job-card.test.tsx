import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { JobCard } from "./job-card";
import type { JobEnvelope } from "@/types/data-tab";

// The SSE stream is irrelevant to the card's render/cancel behavior — stub it out so the
// test drives purely off the JobEnvelope.
vi.mock("./use-job-stream", () => ({
  useJobStream: () => {},
}));

const { deleteJobSpy, FakeDataApiError } = vi.hoisted(() => {
  class FakeDataApiError extends Error {
    constructor(public status: number, public code: string | undefined, message: string) {
      super(message);
    }
  }
  return { deleteJobSpy: vi.fn(), FakeDataApiError };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: { deleteJob: (...args: unknown[]) => deleteJobSpy(...args) },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

beforeEach(() => {
  deleteJobSpy.mockReset();
});

function makeJob(overrides: Partial<JobEnvelope> = {}): JobEnvelope {
  return {
    job_id: "job-1",
    kind: "load",
    state: "running",
    feed_key: "binance/BTCUSDT/candles/1h",
    created_at: null,
    updated_at: null,
    error: null,
    progress: { phase: null, done: 2, total: 5, detail: { current_month: "2024-03" } },
    ...overrides,
  };
}

function renderCard(job: JobEnvelope) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <JobCard job={job} />
    </QueryClientProvider>,
  );
}

describe("JobCard", () => {
  it("renders a JobEnvelope: kind, feed_key, state, and a progress bar", () => {
    renderCard(makeJob());
    expect(screen.getByText(/binance\/BTCUSDT\/candles\/1h/)).toBeInTheDocument();
    expect(screen.getByText("load")).toBeInTheDocument();
    expect(screen.getByText("running")).toBeInTheDocument();
    const bar = screen.getByRole("progressbar");
    expect(bar).toHaveAttribute("aria-valuenow", "40"); // 2/5
    // Load-kind detail line.
    expect(screen.getByText(/2024-03/)).toBeInTheDocument();
  });

  it("shows the materialize two-stage detail line from progress.detail", () => {
    renderCard(
      makeJob({
        kind: "materialize",
        progress: {
          phase: "materialize",
          done: 1,
          total: 3,
          detail: { stage_index: 0, stages_total: 3, stage: "download" },
        },
      }),
    );
    expect(screen.getByText(/Stage 1 of 3 \(download\)/)).toBeInTheDocument();
  });

  it("shows ✕ for a non-terminal job and calls deleteJob on click", async () => {
    deleteJobSpy.mockResolvedValueOnce(undefined);
    renderCard(makeJob({ state: "running" }));
    const btn = screen.getByRole("button", { name: /cancel job/i });
    fireEvent.click(btn);
    await waitFor(() => expect(deleteJobSpy).toHaveBeenCalledWith("job-1"));
  });

  it("hides ✕ for a terminal job", () => {
    renderCard(makeJob({ state: "complete" }));
    expect(screen.queryByRole("button", { name: /cancel job/i })).toBeNull();
  });

  it("guards a zero-total progress (no progress bar rendered)", () => {
    renderCard(makeJob({ progress: { phase: null, done: 0, total: 0, detail: null } }));
    expect(screen.queryByRole("progressbar")).toBeNull();
  });

  it("renders an error banner when the job carries an error", () => {
    renderCard(
      makeJob({ state: "error", error: { code: "boom", message: "Download failed" } }),
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Download failed");
  });
});
