// Phase 3 — Data tab API client. Hits /api/data/* on the main WebApi (which proxies to
// HistoryLoader). Mirrors the endpoint shape one-for-one so the surface is easy to scan.

import type {
  AggregateAcceptedResponse,
  AggregateRequest,
  AggregationOptionsResponse,
  AssetCatalogEntry,
  AssetListResponse,
  ExchangeListResponse,
  FeedStatusResponse,
  JobSnapshot,
} from "@/types/data-tab";

const BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

class DataApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string | undefined,
    message: string,
    public readonly body: unknown,
  ) {
    super(message);
    this.name = "DataApiError";
  }
}

async function asJson<T>(resp: Response): Promise<T> {
  if (resp.ok) return (await resp.json()) as T;
  // ProblemDetails / domain-error body — surface the `code` field (FE branches on it).
  let body: unknown = null;
  let code: string | undefined;
  try {
    body = await resp.json();
    code = (body as { code?: string }).code;
  } catch {
    // Non-JSON response (e.g. plain-text 504); fall through with body=null.
  }
  throw new DataApiError(
    resp.status,
    code,
    `${resp.status} ${resp.statusText}` + (code ? ` (${code})` : ""),
    body,
  );
}

export const dataApi = {
  // -------- Catalog --------
  getExchanges: (signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/exchanges`, { signal }).then(asJson<ExchangeListResponse>),

  getAssetsByExchange: (exchange: string, signal?: AbortSignal) =>
    fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets`,
      { signal },
    ).then(asJson<AssetListResponse>),

  getAssets: (signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/assets`, { signal }).then(asJson<AssetListResponse>),

  // -------- Per-feed status / eligibility --------
  getFeedStatus: (
    exchange: string,
    asset: string,
    feedId: string,
    signal?: AbortSignal,
  ) =>
    fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets/${encodeURIComponent(asset)}/feeds/${encodeURIComponent(feedId)}/status`,
      { signal },
    ).then(asJson<FeedStatusResponse>),

  getAggregationOptions: (
    exchange: string,
    asset: string,
    feedId: string,
    signal?: AbortSignal,
  ) =>
    fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets/${encodeURIComponent(asset)}/feeds/${encodeURIComponent(feedId)}/aggregation-options`,
      { signal },
    ).then(asJson<AggregationOptionsResponse>),

  // -------- Aggregate / delete --------
  postAggregate: async (
    exchange: string,
    asset: string,
    body: AggregateRequest,
    signal?: AbortSignal,
  ): Promise<AggregateAcceptedResponse> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets/${encodeURIComponent(asset)}/aggregate`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal,
      },
    );
    return asJson<AggregateAcceptedResponse>(resp);
  },

  deleteFeed: async (
    exchange: string,
    asset: string,
    feedId: string,
    signal?: AbortSignal,
  ): Promise<void> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets/${encodeURIComponent(asset)}/feeds/${encodeURIComponent(feedId)}`,
      { method: "DELETE", signal },
    );
    if (!resp.ok) await asJson(resp);  // throws DataApiError
  },

  // -------- Job snapshot --------
  getJobSnapshot: (jobId: string, signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/aggregations/${encodeURIComponent(jobId)}`, { signal })
      .then(asJson<JobSnapshot>),

  // Phase 6 — cancel an active job. 204 on success, 404 if job unknown / retention expired,
  // 409 if already terminal. The job's SSE stream emits a `cancelled` terminal event before
  // closing — useJobStream handles cleanup; callers don't need to clear state themselves.
  cancelJob: async (jobId: string, signal?: AbortSignal): Promise<void> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/aggregations/${encodeURIComponent(jobId)}`,
      { method: "DELETE", signal },
    );
    if (!resp.ok) await asJson(resp);  // throws DataApiError carrying ProblemDetails body
  },
};

export { DataApiError };
export type { AssetCatalogEntry };
