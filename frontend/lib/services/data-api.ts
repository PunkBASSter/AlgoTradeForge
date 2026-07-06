// Data tab API client. Hits /api/data/* on the main WebApi (which proxies to
// HistoryLoader).

import type {
  AggregateRequest,
  AggregateResponse,
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
  // Surface ProblemDetails `code` so callers can branch on it; tolerate non-JSON bodies.
  let body: unknown = null;
  let code: string | undefined;
  try {
    body = await resp.json();
    code = (body as { code?: string }).code;
  } catch { /* non-JSON body */ }
  throw new DataApiError(
    resp.status,
    code,
    `${resp.status} ${resp.statusText}` + (code ? ` (${code})` : ""),
    body,
  );
}

export const dataApi = {
  getExchanges: (signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/exchanges`, { signal }).then(asJson<ExchangeListResponse>),

  getAssetsByExchange: (exchange: string, signal?: AbortSignal) =>
    fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets`,
      { signal },
    ).then(asJson<AssetListResponse>),

  getAssets: (signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/assets`, { signal }).then(asJson<AssetListResponse>),

  refreshCatalog: async (signal?: AbortSignal): Promise<void> => {
    const resp = await fetch(`${BASE_URL}/api/data/refresh`, { method: "POST", signal });
    if (!resp.ok) await asJson(resp);
  },

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

  postAggregate: async (
    exchange: string,
    asset: string,
    body: AggregateRequest,
    signal?: AbortSignal,
  ): Promise<AggregateResponse> => {
    // 202 = queued job; 200 = no_new_data. Callers narrow on `"job_id" in resp`.
    const resp = await fetch(
      `${BASE_URL}/api/data/exchanges/${encodeURIComponent(exchange)}/assets/${encodeURIComponent(asset)}/aggregate`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal,
      },
    );
    return asJson<AggregateResponse>(resp);
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
    if (!resp.ok) await asJson(resp);
  },

  getJobSnapshot: (jobId: string, signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/aggregations/${encodeURIComponent(jobId)}`, { signal })
      .then(asJson<JobSnapshot>),

  // Cancel an active job: 204 success, 404 unknown/retention expired, 409 already
  // terminal. The SSE stream emits a `cancelled` terminal event before closing; callers
  // don't need to clear state — useJobStream handles it.
  cancelJob: async (jobId: string, signal?: AbortSignal): Promise<void> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/aggregations/${encodeURIComponent(jobId)}`,
      { method: "DELETE", signal },
    );
    if (!resp.ok) await asJson(resp);
  },
};

export { DataApiError };
export type { AssetCatalogEntry };
