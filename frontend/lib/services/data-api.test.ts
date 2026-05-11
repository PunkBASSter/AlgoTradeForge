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
