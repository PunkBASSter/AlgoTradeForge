import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { NewAggregateForm } from "./new-aggregate-form";
import type { AggregateRequest, FeedCatalogEntry } from "@/types/data-tab";

// P6-17 — Phase 6 source dropdown for re-aggregation. Verifies:
// 1. With only the primary source, the field renders as a static display (no dropdown noise).
// 2. With eligibleSources (alt-bar feeds in the same row), the field renders as a <select>.
// 3. Selecting a different source re-keys the eligibility query so the type dropdown
//    re-populates from the new source's eligible_types.

const fetchMock = vi.fn();
beforeEach(() => {
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  fetchMock.mockReset();
});

afterEach(() => {
  // restore between tests
  fetchMock.mockReset();
});

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

const tb = (id: string, interval: string): FeedCatalogEntry => ({
  id, kind: "OHLCV_TimeBar", interval, type_code: null, threshold_value: null, sidecar: null,
});
const alt = (id: string, type: string, threshold: number): FeedCatalogEntry => ({
  id, kind: "OHLCV_AltBar", interval: null, type_code: type, threshold_value: threshold, sidecar: null,
});

function renderForm(
  sourceFeed: FeedCatalogEntry,
  eligibleSources?: FeedCatalogEntry[],
) {
  // New QueryClient per test so cache state doesn't leak.
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <NewAggregateForm
        exchange="binance"
        asset="BTCUSDT"
        sourceFeed={sourceFeed}
        eligibleSources={eligibleSources}
      />
    </QueryClientProvider>,
  );
}

function mockEligibility(eligible: string[]) {
  fetchMock.mockResolvedValueOnce(
    new Response(JSON.stringify({ eligible_types: eligible, ineligible: [], warnings: [] }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  );
}

describe("NewAggregateForm Source field", () => {
  it("renders Source as static text when only the primary source is available", async () => {
    mockEligibility(["EqV", "EqT", "EqD"]);
    renderForm(tb("1m", "1m"));

    // Single-option case: no <select> — just a font-mono span with the id.
    await waitFor(() => expect(screen.queryByDisplayValue("1m")).toBeNull());
    expect(screen.getByText("1m")).toBeInTheDocument();
  });

  it("renders Source as <select> when eligibleSources are provided", async () => {
    mockEligibility(["EqV", "EqT", "EqD"]);
    const altSource = alt("EqV_1m_1000", "EqV", 1000);
    renderForm(tb("1m", "1m"), [altSource]);

    await waitFor(() => {
      const select = screen.getByDisplayValue("1m") as HTMLSelectElement;
      expect(select.tagName).toBe("SELECT");
    });

    const select = screen.getByDisplayValue("1m") as HTMLSelectElement;
    const optionTexts = Array.from(select.options).map((o) => o.textContent ?? "");
    expect(optionTexts).toContain("1m");
    expect(optionTexts.some((t) => t.includes("EqV_1m_1000"))).toBe(true);
    expect(optionTexts.some((t) => t.includes("re-aggregate"))).toBe(true);
  });

  it("dedupes when eligibleSources contains the same id as primary", async () => {
    mockEligibility(["EqV"]);
    const primary = alt("EqV_1m_1000", "EqV", 1000);
    renderForm(primary, [primary]);

    // Only one option (the primary), so it's rendered as static text.
    await waitFor(() => expect(screen.queryByDisplayValue("EqV_1m_1000")).toBeNull());
    expect(screen.getByText("EqV_1m_1000")).toBeInTheDocument();
  });

  // Reviewer Issue B3 — submit body must carry the type-correct threshold_unit.
  // EqD's threshold is in quote currency (price × volume); EqT counts records; EqV is base.
  it.each([
    ["EqV", "base_asset"],
    ["EqT", "trades"],
    ["EqD", "quote_asset"],
  ] as const)("submits threshold_unit=%s for typeCode=%s", async (type, expectedUnit) => {
    mockEligibility([type]);
    // Mock the POST /aggregate response so the mutation resolves cleanly.
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ job_id: "j1", state: "queued" }), {
        status: 202, headers: { "Content-Type": "application/json" },
      }),
    );
    renderForm(tb("1m", "1m"));

    // Wait for the eligibility query to resolve and the type option to render — the
    // dropdown is initially disabled with only the placeholder option, so a fireEvent
    // here without waiting would target an empty <select>.
    await screen.findByRole("option", { name: type });
    const typeSelect = screen.getAllByRole("combobox").find((el) =>
      Array.from((el as HTMLSelectElement).options).some((o) => o.value === type),
    ) as HTMLSelectElement;
    fireEvent.change(typeSelect, { target: { value: type } });

    fireEvent.change(screen.getByPlaceholderText(/1k, 500m, 1.5M/), { target: { value: "1k" } });
    fireEvent.click(screen.getByRole("button", { name: /aggregate/i }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));   // POST went out
    const [, postInit] = fetchMock.mock.calls[1];
    const body = JSON.parse((postInit as RequestInit).body as string) as AggregateRequest;
    expect(body.type_code).toBe(type);
    expect(body.threshold_unit).toBe(expectedUnit);
  });

  it("re-fetches eligibility when the user picks a different source", async () => {
    // First mock: response for primary source 1m
    mockEligibility(["EqV", "EqT", "EqD"]);
    // Second mock: response for the alt-bar source after selection change
    mockEligibility(["EqV"]);

    const altSource = alt("EqV_1m_1000", "EqV", 1000);
    renderForm(tb("1m", "1m"), [altSource]);

    await waitFor(() => expect(screen.getByDisplayValue("1m")).toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledTimes(1);

    fireEvent.change(screen.getByDisplayValue("1m"), { target: { value: "EqV_1m_1000" } });

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    // The Type dropdown should now reflect EqV-only eligibility (cross-family rejected server-side).
    await waitFor(() => {
      const typeSelect = screen.getAllByRole("combobox").find((el) =>
        Array.from((el as HTMLSelectElement).options).some((o) => o.value === "EqV"),
      ) as HTMLSelectElement;
      const typeOptions = Array.from(typeSelect.options).map((o) => o.value);
      expect(typeOptions).toContain("EqV");
      expect(typeOptions).not.toContain("EqT");
    });
  });
});
