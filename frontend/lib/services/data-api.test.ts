import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { dataApi, DataApiError } from "./data-api";

// P6-17 — REST surface for the Phase 6 cancel endpoint.
//
// The fetch mock replaces global fetch deterministically per-case so we can drive 204 / 404 /
// 409 paths without a real network. We assert the URL shape (proxy under /api/data/...) and
// the DataApiError thrown on non-2xx with a parsed `code` discriminator.

describe("dataApi.cancelJob", () => {
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

  it("DELETEs /api/data/aggregations/{jobId} and resolves on 204", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await expect(dataApi.cancelJob("abc123")).resolves.toBeUndefined();

    expect(fetchMock).toHaveBeenCalledOnce();
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/aggregations\/abc123$/);
    expect((init as RequestInit).method).toBe("DELETE");
  });

  it("throws DataApiError carrying the job_not_found code on 404", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "job_not_found_or_expired", job_id: "missing" }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.cancelJob("missing")).rejects.toThrowError(DataApiError);
  });

  it("throws DataApiError with the job_already_terminal code on 409", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: "job_already_terminal", state: "complete" }), {
        status: 409,
        headers: { "Content-Type": "application/json" },
      }),
    );

    try {
      await dataApi.cancelJob("j1");
      expect.fail("expected DataApiError");
    } catch (err) {
      expect(err).toBeInstanceOf(DataApiError);
      expect((err as DataApiError).code).toBe("job_already_terminal");
      expect((err as DataApiError).status).toBe(409);
    }
  });

  it("URL-encodes the jobId path component", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await dataApi.cancelJob("with/slash/and space");

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain("with%2Fslash%2Fand%20space");
  });
});

// Continuation contract: postAggregate accepts both 202 (job queued) and 200 (no_new_data)
// as success shapes. The TS type union forces callers to narrow; this suite pins both
// shapes wire-side so a server-side rename of either field surfaces here.
describe("dataApi.postAggregate response narrowing", () => {
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

  it("returns AggregateAcceptedResponse on 202", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ job_id: "j1", state: "queued" }), {
        status: 202,
        headers: { "Content-Type": "application/json" },
      }),
    );
    const resp = await dataApi.postAggregate("binance", "BTCUSDT", {
      source_feed_id: "1m",
      type_code: "EqV",
      threshold: null,
      threshold_unit: "base_asset",
      input_mode: "convenience",
      convenience_input: "1k",
    });
    expect("job_id" in resp).toBe(true);
    if ("job_id" in resp) expect(resp.job_id).toBe("j1");
  });

  it("returns AggregateNoOpResponse on 200 with code=no_new_data", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          code: "no_new_data",
          feed_id: "EqV_1m_1000",
          last_source_ts: 1234567890,
          last_bar_ts: "1234567000",
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );
    const resp = await dataApi.postAggregate("binance", "BTCUSDT", {
      source_feed_id: "1m",
      type_code: "EqV",
      threshold: null,
      threshold_unit: "base_asset",
      input_mode: "convenience",
      convenience_input: "1k",
    });
    expect("job_id" in resp).toBe(false);
    if (!("job_id" in resp)) {
      expect(resp.code).toBe("no_new_data");
      expect(resp.feed_id).toBe("EqV_1m_1000");
    }
  });

  it("propagates 422 resume_unsupported as DataApiError", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          code: "resume_unsupported",
          feed_id: "EqV_1m_1000",
          message: "legacy",
        }),
        { status: 422, headers: { "Content-Type": "application/json" } },
      ),
    );
    await expect(
      dataApi.postAggregate("binance", "BTCUSDT", {
        source_feed_id: "1m",
        type_code: "EqV",
        threshold: null,
        threshold_unit: "base_asset",
        input_mode: "convenience",
        convenience_input: "1k",
      }),
    ).rejects.toThrowError(DataApiError);
  });
});

describe("dataApi.getJobs", () => {
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

  it("GETs /api/data/jobs with no query string when params are omitted", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([]), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.getJobs()).resolves.toEqual([]);

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/jobs$/);
    expect((init as RequestInit | undefined)?.method).toBeUndefined(); // default GET
  });

  it("appends kind query param when provided", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([]), { status: 200, headers: { "Content-Type": "application/json" } }),
    );

    await dataApi.getJobs({ kind: "materialize" });

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/jobs\?kind=materialize$/);
  });

  it("appends both kind and state query params when provided", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([]), { status: 200, headers: { "Content-Type": "application/json" } }),
    );

    await dataApi.getJobs({ kind: "load", state: "running" });

    const [url] = fetchMock.mock.calls[0];
    const urlStr = String(url);
    expect(urlStr).toContain("kind=load");
    expect(urlStr).toContain("state=running");
  });

  it("throws DataApiError on 500", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: "internal" }), {
        status: 500,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.getJobs()).rejects.toThrowError(DataApiError);
  });
});

describe("dataApi.getJob", () => {
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

  it("GETs /api/data/jobs/{id} and returns the envelope", async () => {
    const envelope = {
      job_id: "j42",
      kind: "materialize",
      state: "running",
      feed_key: "binance/BTCUSDT_perp/klines",
      created_at: null,
      updated_at: null,
      error: null,
      progress: { phase: "load", done: 3, total: 10, detail: null },
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(envelope), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const result = await dataApi.getJob("j42");

    expect(result.job_id).toBe("j42");
    expect(result.kind).toBe("materialize");
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/jobs\/j42$/);
  });

  it("URL-encodes the id path component", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({}), { status: 200, headers: { "Content-Type": "application/json" } }),
    );

    await dataApi.getJob("with/slash");

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain("with%2Fslash");
  });

  it("throws DataApiError on 404", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: "not_found" }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.getJob("missing")).rejects.toThrowError(DataApiError);
  });
});

describe("dataApi.postMaterialize", () => {
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

  it("POSTs /api/data/materialize with JSON body and returns job_id + location on 202", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ job_id: "mat-99", location: "/api/v1/jobs/mat-99/progress" }),
        { status: 202, headers: { "Content-Type": "application/json" } },
      ),
    );

    const result = await dataApi.postMaterialize({
      exchange: "binance",
      symbol: "BTCUSDT",
      feed: "klines_1m",
    });

    expect(result.job_id).toBe("mat-99");
    expect(result.location).toBe("/api/v1/jobs/mat-99/progress");

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/materialize$/);
    expect((init as RequestInit).method).toBe("POST");
    expect((init as RequestInit).headers).toMatchObject({ "Content-Type": "application/json" });
    expect(JSON.parse((init as RequestInit).body as string)).toMatchObject({
      exchange: "binance",
      symbol: "BTCUSDT",
      feed: "klines_1m",
    });
  });

  it("throws DataApiError with feed_not_materializable code on 422", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ code: "feed_not_materializable", message: "no derived feed" }),
        { status: 422, headers: { "Content-Type": "application/json" } },
      ),
    );

    try {
      await dataApi.postMaterialize({ exchange: "binance", symbol: "ETHUSDT", feed: "klines_1m" });
      expect.fail("expected DataApiError");
    } catch (err) {
      expect(err).toBeInstanceOf(DataApiError);
      expect((err as DataApiError).code).toBe("feed_not_materializable");
      expect((err as DataApiError).status).toBe(422);
    }
  });

  it("throws DataApiError with feed_busy code on 409", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ code: "feed_busy", active_job_id: "other-job" }),
        { status: 409, headers: { "Content-Type": "application/json" } },
      ),
    );

    await expect(
      dataApi.postMaterialize({ exchange: "binance", symbol: "BTCUSDT", feed: "klines_1m" }),
    ).rejects.toThrowError(DataApiError);
  });
});

describe("dataApi.deleteJob", () => {
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

  it("DELETEs /api/data/jobs/{id} and resolves on 204", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await expect(dataApi.deleteJob("job-7")).resolves.toBeUndefined();

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/jobs\/job-7$/);
    expect((init as RequestInit).method).toBe("DELETE");
  });

  it("URL-encodes the id path component", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await dataApi.deleteJob("with/slash/and space");

    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain("with%2Fslash%2Fand%20space");
  });

  it("throws DataApiError on 404", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ code: "not_found" }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(dataApi.deleteJob("missing")).rejects.toThrowError(DataApiError);
  });
});

describe("dataApi.deleteFeed", () => {
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

  it("DELETEs /api/data/exchanges/{ex}/assets/{a}/feeds/{feedId} and resolves on 204", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await expect(dataApi.deleteFeed("binance", "BTCUSDT", "EqV_1m_1000")).resolves.toBeUndefined();

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toMatch(/\/api\/data\/exchanges\/binance\/assets\/BTCUSDT\/feeds\/EqV_1m_1000$/);
    expect((init as RequestInit).method).toBe("DELETE");
  });

  it("throws DataApiError on 423 feed_already_locked, preserving the existing_job_id in body", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          code: "feed_already_locked",
          feed_id: "EqV_1m_1000",
          existing_job_id: "abcd1234",
          existing_job_state: "running",
        }),
        { status: 423, headers: { "Content-Type": "application/json" } },
      ),
    );

    try {
      await dataApi.deleteFeed("binance", "BTCUSDT", "EqV_1m_1000");
      expect.fail("expected DataApiError");
    } catch (err) {
      expect(err).toBeInstanceOf(DataApiError);
      const apiErr = err as DataApiError;
      expect(apiErr.code).toBe("feed_already_locked");
      expect(apiErr.status).toBe(423);
      expect((apiErr.body as { existing_job_id: string }).existing_job_id).toBe("abcd1234");
    }
  });
});
