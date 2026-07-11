import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { GroupsPanel } from "../groups-panel";
import type { CollectionGroupSummary, DesiredStateReport, TupleStatus } from "@/types/data-tab";

// Mock CodeMirror — GroupEditor (inside SlideOver) requires it.
vi.mock("codemirror", () => ({ basicSetup: [] }));
vi.mock("@codemirror/lang-json", () => ({ json: () => [], jsonParseLinter: () => () => [] }));
vi.mock("@codemirror/theme-one-dark", () => ({ oneDark: [] }));
vi.mock("@codemirror/lint", () => ({ linter: () => [] }));
vi.mock("@codemirror/view", () => {
  class EditorViewMock {
    static theme() { return []; }
    static updateListener = { of: () => [] };
    destroy() {}
    get state() { return { doc: { toString: () => "", length: 0 } }; }
    dispatch() {}
  }
  return { EditorView: EditorViewMock };
});
vi.mock("@codemirror/state", () => ({
  EditorState: { create: () => ({}) },
}));

const { getGroupsSpy, getDesiredStateSpy, FakeDataApiError } = vi.hoisted(() => {
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
  return { getGroupsSpy: vi.fn(), getDesiredStateSpy: vi.fn(), FakeDataApiError };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    getGroups: (...args: unknown[]) => getGroupsSpy(...args),
    getDesiredState: (...args: unknown[]) => getDesiredStateSpy(...args),
    getGroup: vi.fn().mockResolvedValue({ group: {}, etag: undefined }),
    validateGroup: vi
      .fn()
      .mockResolvedValue({
        errors: [],
        expansion: { tuple_count: 0, per_exchange: [], unsupported: [], conflicts: [], already_materialized: 0 },
      }),
    putGroup: vi.fn().mockResolvedValue({ etag: undefined }),
    deleteGroup: vi.fn().mockResolvedValue(undefined),
  },
  DataApiError: FakeDataApiError,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

beforeEach(() => {
  getGroupsSpy.mockReset();
  getDesiredStateSpy.mockReset();
});

function renderPanel() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <GroupsPanel />
    </QueryClientProvider>,
  );
}

const MOCK_GROUPS: { groups: CollectionGroupSummary[] } = {
  groups: [
    {
      name: "my-group",
      enabled: true,
      exchanges: ["binance"],
      symbol_count: 2,
      feed_count: 3,
      etag: 'W/"abc"',
    },
  ],
};

function makeTuple(overrides: Partial<TupleStatus> = {}): TupleStatus {
  return {
    exchange: "binance",
    canonical: "BTC/USDT-PERP",
    dir: "BTCUSDT_perp",
    feed_name: "candles",
    interval: "1h",
    status: "materialized",
    months_expected: 12,
    months_covered: 12,
    collect: "eager",
    history_start: "2025-01",
    is_derived: false,
    groups: ["my-group"],
    ...overrides,
  };
}

const MOCK_DESIRED_STATE: DesiredStateReport = {
  computed_at: "2026-07-10T00:00:00Z",
  tuples: [
    makeTuple({ status: "materialized" }),
    makeTuple({ feed_name: "candles", interval: "1m", status: "partial" }),
    makeTuple({ canonical: "ETH/USDT-PERP", feed_name: "candles", interval: "1h", status: "missing" }),
    makeTuple({ canonical: "ETH/USDT-PERP", feed_name: "funding-rate", interval: "", status: "on-demand" }),
    // Different group — must NOT affect "my-group" counts.
    makeTuple({ canonical: "SOL/USDT-PERP", status: "missing", groups: ["other-group"] }),
  ],
  orphaned: [],
  orphaned_total: 0,
  conflicts: [],
};

describe("GroupsPanel", () => {
  it("renders cards from mocked queries with convergence counts", async () => {
    getGroupsSpy.mockResolvedValueOnce(MOCK_GROUPS);
    getDesiredStateSpy.mockResolvedValueOnce(MOCK_DESIRED_STATE);

    renderPanel();

    await waitFor(() => screen.getByText("my-group"));

    expect(screen.getByText(/1 materialized/i)).toBeDefined();
    expect(screen.getByText(/1 partial/i)).toBeDefined();
    expect(screen.getByText(/1 missing/i)).toBeDefined();
  });

  it("on-demand tuples shown as neutral chip, not counted in missing", async () => {
    getGroupsSpy.mockResolvedValueOnce(MOCK_GROUPS);
    getDesiredStateSpy.mockResolvedValueOnce(MOCK_DESIRED_STATE);

    renderPanel();

    await waitFor(() => screen.getByText("my-group"));

    // on-demand chip is present
    expect(screen.getByText(/1 on.demand/i)).toBeDefined();

    // missing count stays at 1 (on-demand not counted)
    expect(screen.getByText(/1 missing/i)).toBeDefined();
    expect(screen.queryByText(/2 missing/i)).toBeNull();
  });
});
