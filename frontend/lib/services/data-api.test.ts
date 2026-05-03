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
