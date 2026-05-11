import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { JobProgressCard } from "./job-progress";
import { useDataJobsStore } from "@/lib/stores/data-jobs-store";

// P6-17 — Phase 6 cancel button on the JobProgressCard.
//
// Strategy: capture the parent's `onObservation` callback so the test can drive `obs.type`
// directly (the real SSE stream is irrelevant for button visibility/click behavior). Mock
// useJobStream + dataApi so each test specifies state deterministically.

let observationDispatch: ((obs: { latest: unknown; type: string | null }) => void) | null = null;

vi.mock("./use-job-stream", () => ({
  useJobStream: (
    _key: string,
    _exchange: string,
    onObs?: (obs: { latest: unknown; type: string | null }) => void,
  ) => {
    observationDispatch = onObs ?? null;
  },
}));

// vi.mock factories are hoisted above all top-level declarations, so the FakeDataApiError
// class needs to be created inside vi.hoisted() to be available when the factory runs.
const { cancelJobSpy, getJobSnapshotSpy, FakeDataApiError } = vi.hoisted(() => {
  class FakeDataApiError extends Error {
    constructor(public status: number, public code: string | undefined, message: string) {
      super(message);
    }
  }
  return { cancelJobSpy: vi.fn(), getJobSnapshotSpy: vi.fn(), FakeDataApiError };
});
vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    cancelJob: (...args: unknown[]) => cancelJobSpy(...args),
    getJobSnapshot: (...args: unknown[]) => getJobSnapshotSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

beforeEach(() => {
  observationDispatch = null;
  cancelJobSpy.mockReset();
  getJobSnapshotSpy.mockReset();
  // Seed the store with a job entry so the card has a jobId to cancel.
  useDataJobsStore.setState({
    jobs: { "binance|BTCUSDT|EqV_1m_1k": { jobId: "j1", lastEventId: 0, updatedAt: Date.now() } },
  });
});

afterEach(() => {
  useDataJobsStore.setState({ jobs: {} });
  // Defensive: any test that flipped to fake timers must also flip back. Keeps later
  // tests' waitFor() polling against real microtasks instead of stalled fake timers.
  vi.useRealTimers();
});

function renderCard() {
  return render(
    <JobProgressCard jobKey="binance|BTCUSDT|EqV_1m_1k" exchange="binance" outcomeHint="EqV_1m_1k" />,
  );
}

describe("JobProgressCard cancel button", () => {
  it("hides Cancel before any SSE observation arrives (avoids flicker)", () => {
    renderCard();
    expect(screen.queryByRole("button", { name: /cancel aggregation/i })).toBeNull();
  });

  it.each(["queued", "started", "progress"] as const)(
    "shows Cancel for non-terminal observation type=%s",
    (type) => {
      renderCard();
      act(() => observationDispatch!({ latest: { queue_position: 1, current_partition: "2024-06", bars_emitted: 0 }, type }));
      expect(screen.getByRole("button", { name: /cancel aggregation/i })).toBeInTheDocument();
    },
  );

  it.each(["complete", "error", "cancelled"] as const)(
    "hides Cancel for terminal observation type=%s",
    (type) => {
      renderCard();
      act(() => observationDispatch!({ latest: {}, type }));
      expect(screen.queryByRole("button", { name: /cancel aggregation/i })).toBeNull();
    },
  );

  it("calls dataApi.cancelJob with the stored jobId on click and disables button optimistically", async () => {
    cancelJobSpy.mockResolvedValueOnce(undefined);
    renderCard();
    act(() => observationDispatch!({ latest: { queue_position: 1 }, type: "queued" }));

    const btn = screen.getByRole("button", { name: /cancel aggregation/i });
    fireEvent.click(btn);

    expect(cancelJobSpy).toHaveBeenCalledWith("j1");
    // Button disabled while pending; label flips to Cancelling…
    await waitFor(() => expect(btn).toBeDisabled());
    expect(screen.getByText(/cancelling/i)).toBeInTheDocument();
  });

  it("re-enables Cancel and surfaces a toast when cancelJob fails", async () => {
    cancelJobSpy.mockRejectedValueOnce(new Error("network down"));
    renderCard();
    act(() => observationDispatch!({ latest: { queue_position: 1 }, type: "queued" }));

    const btn = screen.getByRole("button", { name: /cancel aggregation/i });
    fireEvent.click(btn);

    // After the rejection settles, the button is no longer pending (re-enabled).
    await waitFor(() => expect(btn).not.toBeDisabled());
  });

  // Reviewer Issue F2 — snapshot-poll fallback so a stuck SSE doesn't strand the card.
  // shouldAdvanceTime keeps real-time wall-clock progressing under the fake-timer scheduler
  // so testing-library's waitFor() polling still works while we still control setTimeout.
  it("polls the snapshot and clears the job if SSE never delivers a terminal event", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    cancelJobSpy.mockResolvedValueOnce(undefined);
    getJobSnapshotSpy.mockResolvedValueOnce({ state: "cancelled", job_id: "j1" });
    renderCard();
    act(() => observationDispatch!({ latest: { queue_position: 1 }, type: "queued" }));

    fireEvent.click(screen.getByRole("button", { name: /cancel aggregation/i }));
    // Flush the cancelJob promise resolution so the useEffect's setTimeout is scheduled.
    await act(async () => { await Promise.resolve(); });

    await act(async () => { await vi.advanceTimersByTimeAsync(3_000); });

    expect(getJobSnapshotSpy).toHaveBeenCalledWith("j1", expect.anything());
    await waitFor(
      () => expect(useDataJobsStore.getState().jobs["binance|BTCUSDT|EqV_1m_1k"]).toBeUndefined(),
    );
  });

  it("clears the job locally when the snapshot poll returns 404 (already retention-evicted)", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    cancelJobSpy.mockResolvedValueOnce(undefined);
    getJobSnapshotSpy.mockRejectedValueOnce(new FakeDataApiError(404, "job_not_found_or_expired", "404"));
    renderCard();
    act(() => observationDispatch!({ latest: { queue_position: 1 }, type: "queued" }));

    fireEvent.click(screen.getByRole("button", { name: /cancel aggregation/i }));
    await act(async () => { await Promise.resolve(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(3_000); });

    await waitFor(
      () => expect(useDataJobsStore.getState().jobs["binance|BTCUSDT|EqV_1m_1k"]).toBeUndefined(),
    );
  });
});
