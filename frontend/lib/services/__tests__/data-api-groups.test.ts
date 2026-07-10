import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { dataApi, DataApiError } from "../data-api";
import type { CollectionGroupDoc } from "@/types/data-tab";

// Helper: a minimal valid CollectionGroupDoc used across suites.
const minDoc: CollectionGroupDoc = {
  name: "g1",
  enabled: true,
  exchanges: ["binance"],
  assets: { symbols: ["BTCUSDT"], historyStart: "2023-01" },
  feeds: { klines: { collect: "eager" } },
};

// ---- getGroups ----

describe("dataApi.getGroups", () => {
  let originalFetch: typeof fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("GETs /api/data/groups and returns the groups array", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          groups: [
            { name: "g1", enabled: true, exchanges: ["binance"], symbol_count: 2, feed_count: 1, etag: "abc" },
          ],
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );

    const result = await dataApi.getGroups();

    expect(result.groups).toHaveLength(1);
    expect(result.groups[0].name).toBe("g1");
    expect(result.groups[0].symbol_count).toBe(2);

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/groups$/);
  });

  it("throws DataApiError on non-2xx", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "not_ready" }), {
        status: 503,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.getGroups()).rejects.toThrowError(DataApiError);
  });
});

// ---- getGroup ----

describe("dataApi.getGroup", () => {
  let originalFetch: typeof fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("GETs /api/data/groups/{name} and extracts the ETag response header", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(minDoc), {
        status: 200,
        headers: { "Content-Type": "application/json", ETag: '"abc123"' },
      }),
    );

    const result = await dataApi.getGroup("g1");

    expect(result.etag).toBe('"abc123"');
    expect(result.group.name).toBe("g1");
    expect(result.group.assets.historyStart).toBe("2023-01");
  });

  it("URL-encodes the name path component", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ ...minDoc, name: "my group" }), { status: 200 }),
    );

    await dataApi.getGroup("my group");

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain("my%20group");
  });

  it("throws DataApiError on 404", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "group_not_found", name: "missing" }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.getGroup("missing")).rejects.toThrowError(DataApiError);
  });
});

// ---- putGroup ----

describe("dataApi.putGroup", () => {
  let originalFetch: typeof fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("PUTs to /api/data/groups/{name} and returns etag on 200", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ etag: '"newetag"' }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const result = await dataApi.putGroup("g1", minDoc);

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/groups\/g1$/);
    expect((init as RequestInit).method).toBe("PUT");
    expect(result.etag).toBe('"newetag"');
  });

  it("sends If-Match header when etag is provided", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ etag: '"v2"' }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await dataApi.putGroup("g1", minDoc, '"old-etag"');

    const [, init] = fetchMock.mock.calls[0];
    const headers = (init as RequestInit).headers as Record<string, string>;
    expect(headers["If-Match"]).toBe('"old-etag"');
  });

  it("omits If-Match header when etag is not provided", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ etag: '"v1"' }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await dataApi.putGroup("g1", minDoc);

    const [, init] = fetchMock.mock.calls[0];
    const headers = (init as RequestInit).headers as Record<string, string>;
    expect(headers["If-Match"]).toBeUndefined();
  });

  it("throws DataApiError with code concurrency_conflict on 409", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "concurrency_conflict" }), {
        status: 409,
        headers: { "Content-Type": "application/json" },
      }),
    );

    try {
      await dataApi.putGroup("g1", minDoc, '"old"');
      expect.fail("expected DataApiError");
    } catch (err) {
      expect(err).toBeInstanceOf(DataApiError);
      expect((err as DataApiError).code).toBe("concurrency_conflict");
      expect((err as DataApiError).status).toBe(409);
    }
  });

  it("throws DataApiError with code validation_failed on 422 and body carries errors[]", async () => {
    const payload = { error: "validation_failed", errors: ["name is invalid"] };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(payload), {
        status: 422,
        headers: { "Content-Type": "application/json" },
      }),
    );

    try {
      await dataApi.putGroup("g1", minDoc);
      expect.fail("expected DataApiError");
    } catch (err) {
      expect(err).toBeInstanceOf(DataApiError);
      expect((err as DataApiError).code).toBe("validation_failed");
      expect((err as DataApiError).status).toBe(422);
      expect(
        ((err as DataApiError).body as { errors?: string[] }).errors,
      ).toEqual(["name is invalid"]);
    }
  });
});

// ---- deleteGroup ----

describe("dataApi.deleteGroup", () => {
  let originalFetch: typeof fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("DELETEs /api/data/groups/{name} and resolves on 204", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await expect(dataApi.deleteGroup("g1")).resolves.toBeUndefined();

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/groups\/g1$/);
    expect((init as RequestInit).method).toBe("DELETE");
  });

  it("throws DataApiError on 404", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "group_not_found", name: "g1" }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.deleteGroup("g1")).rejects.toThrowError(DataApiError);
  });
});

// ---- validateGroup ----

describe("dataApi.validateGroup", () => {
  let originalFetch: typeof fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("POSTs to /api/data/groups/validate and returns ValidatePreview on 200", async () => {
    const preview = {
      errors: [],
      expansion: {
        tuple_count: 3,
        unsupported: [],
        conflicts: [],
        per_exchange: [{ exchange: "binance", symbols: 1, feeds: 3 }],
        already_materialized: 0,
      },
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(preview), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const result = await dataApi.validateGroup(minDoc);

    expect(result.expansion.tuple_count).toBe(3);
    expect(result.errors).toHaveLength(0);
    expect(result.expansion.per_exchange[0].exchange).toBe("binance");

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/groups\/validate$/);
    expect((init as RequestInit).method).toBe("POST");
  });

  it("returns non-empty errors alongside expansion when validation warnings present", async () => {
    const preview = {
      errors: ["feeds must not be empty"],
      expansion: {
        tuple_count: 0,
        unsupported: [],
        conflicts: [],
        per_exchange: [],
        already_materialized: 0,
      },
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(preview), { status: 200, headers: { "Content-Type": "application/json" } }),
    );

    const result = await dataApi.validateGroup(minDoc);

    expect(result.errors).toHaveLength(1);
    expect(result.expansion.tuple_count).toBe(0);
  });
});

// ---- getDesiredState ----

describe("dataApi.getDesiredState", () => {
  let originalFetch: typeof fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("GETs /api/data/desired-state without exchange filter", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          computed_at: "2024-01-01T00:00:00Z",
          tuples: [],
          orphaned: [],
          orphaned_total: 0,
          conflicts: [],
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );

    const result = await dataApi.getDesiredState();

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/desired-state$/);
    expect(result.tuples).toHaveLength(0);
    expect(result.orphaned_total).toBe(0);
    expect(result.computed_at).toBe("2024-01-01T00:00:00Z");
  });

  it("appends exchange query param when provided", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          computed_at: "2024-01-01T00:00:00Z",
          tuples: [],
          orphaned: [],
          orphaned_total: 0,
          conflicts: [],
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );

    await dataApi.getDesiredState("binance");

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain("exchange=binance");
    expect(String(url)).not.toMatch(/\/api\/data\/desired-state$/);
  });

  it("URL-encodes the exchange query param value", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ computed_at: "", tuples: [], orphaned: [], orphaned_total: 0, conflicts: [] }),
        { status: 200 },
      ),
    );

    await dataApi.getDesiredState("my exchange");

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain("exchange=my%20exchange");
  });

  it("surfaces tuple status values including on-demand", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          computed_at: "2024-01-01T00:00:00Z",
          tuples: [
            {
              exchange: "binance",
              canonical: "BTCUSDT",
              dir: "BTCUSDT_perp",
              feed_name: "klines",
              interval: "1m",
              status: "on-demand",
              months_expected: 12,
              months_covered: 0,
              collect: "on-demand",
              history_start: "2023-01",
              is_derived: false,
              groups: ["g1"],
            },
          ],
          orphaned: [],
          orphaned_total: 0,
          conflicts: [],
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );

    const result = await dataApi.getDesiredState();

    expect(result.tuples[0].status).toBe("on-demand");
    expect(result.tuples[0].groups).toEqual(["g1"]);
    expect(result.tuples[0].is_derived).toBe(false);
  });
});
