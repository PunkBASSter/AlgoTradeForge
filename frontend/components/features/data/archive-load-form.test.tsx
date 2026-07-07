import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ArchiveLoadForm } from "./archive-load-form";
import { useLoadJobsStore } from "@/lib/stores/load-jobs-store";
import type { LoadRequestBody } from "@/types/data-tab";

// vi.mock factories are hoisted above top-level declarations; FakeDataApiError must be
// created inside vi.hoisted() so it is in scope when the factory runs.
const { postLoadSpy, FakeDataApiError } = vi.hoisted(() => {
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
  return { postLoadSpy: vi.fn(), FakeDataApiError };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    postLoad: (...args: unknown[]) => postLoadSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

beforeEach(() => {
  postLoadSpy.mockReset();
  useLoadJobsStore.setState({ jobs: {} });
});

afterEach(() => {
  useLoadJobsStore.setState({ jobs: {} });
});

function renderForm() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ArchiveLoadForm />
    </QueryClientProvider>,
  );
}

describe("ArchiveLoadForm", () => {
  it("posts the exact LoadRequestBody including month-to-date conversion", async () => {
    postLoadSpy.mockResolvedValueOnce({ job_id: "abc123def456" });
    renderForm();

    // Set symbol
    fireEvent.change(screen.getByPlaceholderText("BTCUSDT"), { target: { value: "ETHUSDT" } });

    // Feed → open-interest (perpetual, already selected as asset type)
    const selects = screen.getAllByRole("combobox");
    // selects: [asset_type, feed, interval]
    fireEvent.change(selects[1], { target: { value: "open-interest" } });
    fireEvent.change(selects[2], { target: { value: "5m" } });

    // Month inputs
    fireEvent.change(screen.getByLabelText(/from \(month\)/i), { target: { value: "2024-01" } });
    fireEvent.change(screen.getByLabelText(/to \(month\)/i), { target: { value: "2024-02" } });

    fireEvent.click(screen.getByRole("button", { name: /load/i }));

    await waitFor(() => expect(postLoadSpy).toHaveBeenCalledOnce());

    const [body] = postLoadSpy.mock.calls[0] as [LoadRequestBody, ...unknown[]];
    expect(body).toEqual({
      exchange: "binance",
      symbol: "ETHUSDT",
      asset_type: "perpetual",
      feed_name: "open-interest",
      interval: "5m",
      from: "2024-01-01",
      to: "2024-02-29",
    });
  });

  it("409 — attaches the active_job_id to the store and does not set error banner", async () => {
    postLoadSpy.mockRejectedValueOnce(
      new FakeDataApiError(409, "job_already_active", "409 Conflict", {
        active_job_id: "existing-job-id",
      }),
    );
    renderForm();

    fireEvent.change(screen.getByPlaceholderText("BTCUSDT"), { target: { value: "ETHUSDT" } });
    const selects = screen.getAllByRole("combobox");
    fireEvent.change(selects[1], { target: { value: "open-interest" } });
    fireEvent.change(selects[2], { target: { value: "5m" } });
    fireEvent.change(screen.getByLabelText(/from \(month\)/i), { target: { value: "2024-01" } });
    fireEvent.change(screen.getByLabelText(/to \(month\)/i), { target: { value: "2024-02" } });

    fireEvent.click(screen.getByRole("button", { name: /load/i }));

    await waitFor(() =>
      expect(useLoadJobsStore.getState().jobs["existing-job-id"]).toBeDefined(),
    );
    expect(useLoadJobsStore.getState().jobs["existing-job-id"]?.jobId).toBe("existing-job-id");
    // No error banner for 409
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("inverted months — Load button is disabled and postLoad is never called", () => {
    renderForm();

    fireEvent.change(screen.getByPlaceholderText("BTCUSDT"), { target: { value: "BTCUSDT" } });
    const selects = screen.getAllByRole("combobox");
    fireEvent.change(selects[1], { target: { value: "open-interest" } });
    fireEvent.change(selects[2], { target: { value: "5m" } });
    // from > to: inverted range
    fireEvent.change(screen.getByLabelText(/from \(month\)/i), { target: { value: "2024-06" } });
    fireEvent.change(screen.getByLabelText(/to \(month\)/i), { target: { value: "2024-01" } });

    expect(screen.getByRole("button", { name: /load/i })).toBeDisabled();
    expect(postLoadSpy).not.toHaveBeenCalled();
  });

  it("422 — renders the server error message in a banner", async () => {
    postLoadSpy.mockRejectedValueOnce(
      new FakeDataApiError(422, "not_replenishable", "422 Unprocessable Entity", {
        message: "Feed candles/spot is not replenishable from the Binance archive.",
      }),
    );
    renderForm();

    fireEvent.change(screen.getByPlaceholderText("BTCUSDT"), { target: { value: "ETHUSDT" } });
    const selects = screen.getAllByRole("combobox");
    fireEvent.change(selects[1], { target: { value: "open-interest" } });
    fireEvent.change(selects[2], { target: { value: "5m" } });
    fireEvent.change(screen.getByLabelText(/from \(month\)/i), { target: { value: "2024-01" } });
    fireEvent.change(screen.getByLabelText(/to \(month\)/i), { target: { value: "2024-02" } });

    fireEvent.click(screen.getByRole("button", { name: /load/i }));

    await waitFor(() =>
      expect(
        screen.getByRole("alert"),
      ).toHaveTextContent("Feed candles/spot is not replenishable from the Binance archive."),
    );
  });
});
