import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { GroupEditor } from "../group-editor";
import type { CollectionGroupDoc, ValidatePreview } from "@/types/data-tab";

// Mock CodeMirror — jsdom lacks the DOM APIs CodeMirror requires.
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

const { validateGroupSpy, putGroupSpy, getGroupSpy, FakeDataApiError } = vi.hoisted(() => {
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
    validateGroupSpy: vi.fn(),
    putGroupSpy: vi.fn(),
    getGroupSpy: vi.fn(),
    FakeDataApiError,
  };
});

vi.mock("@/lib/services/data-api", () => ({
  dataApi: {
    validateGroup: (...args: unknown[]) => validateGroupSpy(...args),
    putGroup: (...args: unknown[]) => putGroupSpy(...args),
    getGroup: (...args: unknown[]) => getGroupSpy(...args),
  },
  DataApiError: FakeDataApiError,
}));

const toastSpy = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastSpy }),
}));

beforeEach(() => {
  validateGroupSpy.mockReset();
  putGroupSpy.mockReset();
  getGroupSpy.mockReset();
  toastSpy.mockReset();
});

function renderEditor(props: Partial<Parameters<typeof GroupEditor>[0]> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  const onSaved = vi.fn();
  return {
    onClose,
    onSaved,
    ...render(
      <QueryClientProvider client={qc}>
        <GroupEditor mode="create" onClose={onClose} onSaved={onSaved} {...props} />
      </QueryClientProvider>,
    ),
  };
}

const SAMPLE_GROUP_DOC: CollectionGroupDoc = {
  name: "my-group",
  enabled: true,
  exchanges: ["binance"],
  assets: { symbols: ["BTC/USDT-PERP"], historyStart: "2024-01" },
  feeds: { candles: { collect: "eager", intervals: ["1m", "1h"] } },
};

const SAMPLE_VALIDATE_PREVIEW: ValidatePreview = {
  errors: [],
  expansion: {
    tuple_count: 4,
    per_exchange: [{ exchange: "binance", symbols: 1, feeds: 4 }],
    unsupported: [],
    conflicts: [],
    already_materialized: 0,
  },
};

describe("GroupEditor", () => {
  it("validate flow renders preview when validateGroup returns expansion", async () => {
    validateGroupSpy.mockResolvedValueOnce(SAMPLE_VALIDATE_PREVIEW);

    renderEditor({ mode: "create" });

    fireEvent.click(screen.getByRole("button", { name: /validate/i }));

    await waitFor(() => screen.getByText(/4 tuples/i));
    expect(validateGroupSpy).toHaveBeenCalledOnce();
  });

  it("save sends If-Match etag loaded from getGroup in edit mode", async () => {
    getGroupSpy.mockResolvedValueOnce({ group: SAMPLE_GROUP_DOC, etag: 'W/"abc"' });
    putGroupSpy.mockResolvedValueOnce({ etag: 'W/"def"' });

    renderEditor({ mode: "edit", name: "my-group" });

    // Wait until docText is populated from the loaded group (editor-ready marker appears).
    await waitFor(() => screen.getByTestId("editor-ready"));

    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() => expect(putGroupSpy).toHaveBeenCalledOnce());

    const calledEtag = (putGroupSpy.mock.calls[0] as [string, CollectionGroupDoc, string])[2];
    expect(calledEtag).toBe('W/"abc"');
  });

  it("409 on save shows reload toast", async () => {
    getGroupSpy.mockResolvedValueOnce({ group: SAMPLE_GROUP_DOC, etag: 'W/"abc"' });
    putGroupSpy.mockRejectedValueOnce(
      new FakeDataApiError(409, "concurrency_conflict", "409 Conflict", null),
    );

    renderEditor({ mode: "edit", name: "my-group" });

    // Wait until docText is populated from the loaded group (editor-ready marker appears).
    await waitFor(() => screen.getByTestId("editor-ready"));

    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() =>
      expect(toastSpy).toHaveBeenCalledWith("group changed on server — reload", "error"),
    );
  });
});
