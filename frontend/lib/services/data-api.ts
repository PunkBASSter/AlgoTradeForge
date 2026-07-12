// Data tab API client. Hits /api/data/* on the main WebApi (which proxies to
// HistoryLoader).

import type {
  AggregateRequest,
  AggregateResponse,
  AggregationOptionsResponse,
  AssetCatalogEntry,
  AssetListResponse,
  CollectionGroupDoc,
  CollectionGroupSummary,
  CoverageResponse,
  DesiredStateReport,
  ExchangeListResponse,
  FeedStatusResponse,
  JobEnvelope,
  JobKind,
  JobSnapshot,
  JobState,
  LoadAcceptedResponse,
  LoadJobSnapshotWire,
  LoadRequestBody,
  MaterializeRequest,
  ValidatePreview,
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

  getCoverage: (exchange: string, symbol: string, assetType: string, signal?: AbortSignal) =>
    fetch(
      `${BASE_URL}/api/data/coverage?exchange=${encodeURIComponent(exchange)}&symbol=${encodeURIComponent(symbol)}&asset_type=${encodeURIComponent(assetType)}`,
      { signal },
    ).then(asJson<CoverageResponse>),

  // 202 job accepted. 409 symbol/feed busy surfaces as DataApiError with body
  // { error, active_job_id } — callers attach to the active job instead of failing.
  postLoad: async (body: LoadRequestBody, signal?: AbortSignal): Promise<LoadAcceptedResponse> => {
    const resp = await fetch(`${BASE_URL}/api/data/loads`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    return asJson<LoadAcceptedResponse>(resp);
  },

  getLoadJob: (jobId: string, signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/loads/${encodeURIComponent(jobId)}`, { signal })
      .then(asJson<LoadJobSnapshotWire>),

  // ---- Unified jobs (phase 3b+). ----

  // GET /api/data/jobs — optional kind/state filters; returns all jobs when params omitted.
  getJobs: (params?: { kind?: JobKind; state?: JobState }, signal?: AbortSignal): Promise<JobEnvelope[]> => {
    const qs = new URLSearchParams();
    if (params?.kind !== undefined) qs.set("kind", params.kind);
    if (params?.state !== undefined) qs.set("state", params.state);
    const q = qs.toString();
    return fetch(`${BASE_URL}/api/data/jobs${q ? `?${q}` : ""}`, { signal })
      .then(asJson<JobEnvelope[]>);
  },

  getJob: (id: string, signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/jobs/${encodeURIComponent(id)}`, { signal })
      .then(asJson<JobEnvelope>),

  // 202 job accepted; 409 feed_busy surfaces as DataApiError with body { code, active_job_id }.
  postMaterialize: async (body: MaterializeRequest, signal?: AbortSignal): Promise<{ job_id: string; location: string }> => {
    const resp = await fetch(`${BASE_URL}/api/data/materialize`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    return asJson<{ job_id: string; location: string }>(resp);
  },

  deleteJob: async (id: string, signal?: AbortSignal): Promise<void> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/jobs/${encodeURIComponent(id)}`,
      { method: "DELETE", signal },
    );
    if (!resp.ok) await asJson(resp);
  },

  // ---- Collection groups (declarative data management). ----

  getGroups: (signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/groups`, { signal })
      .then(asJson<{ groups: CollectionGroupSummary[] }>),

  getGroup: async (
    name: string,
    signal?: AbortSignal,
  ): Promise<{ group: CollectionGroupDoc; etag: string | undefined }> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/groups/${encodeURIComponent(name)}`,
      { signal },
    );
    // undefined (not "") on missing header — "" round-tripped into putGroup would send a
    // bogus `If-Match: ""` instead of omitting the header.
    const etag = resp.headers.get("ETag") ?? undefined;
    const group = await asJson<CollectionGroupDoc>(resp);
    return { group, etag };
  },

  putGroup: async (
    name: string,
    body: CollectionGroupDoc,
    etag?: string,
    signal?: AbortSignal,
  ): Promise<{ etag: string }> => {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (etag !== undefined) headers["If-Match"] = etag;
    const resp = await fetch(
      `${BASE_URL}/api/data/groups/${encodeURIComponent(name)}`,
      { method: "PUT", headers, body: JSON.stringify(body), signal },
    );
    if (resp.ok) return asJson<{ etag: string }>(resp);
    let parsedBody: unknown = null;
    try { parsedBody = await resp.json(); } catch { /* non-JSON body */ }
    // Backend uses `error` field (not `code`) for group mutation errors; map it to
    // DataApiError.code so callers can branch on "concurrency_conflict" / "validation_failed".
    const typedBody = parsedBody as { error?: string; code?: string } | null;
    const errorCode = typedBody?.error ?? typedBody?.code;
    throw new DataApiError(
      resp.status,
      errorCode,
      `${resp.status} ${resp.statusText}` + (errorCode ? ` (${errorCode})` : ""),
      parsedBody,
    );
  },

  deleteGroup: async (name: string, signal?: AbortSignal): Promise<void> => {
    const resp = await fetch(
      `${BASE_URL}/api/data/groups/${encodeURIComponent(name)}`,
      { method: "DELETE", signal },
    );
    if (!resp.ok) await asJson(resp);
  },

  validateGroup: (body: CollectionGroupDoc, signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/groups/validate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    }).then(asJson<ValidatePreview>),

  getDesiredState: (exchange?: string, signal?: AbortSignal) => {
    const url = exchange !== undefined
      ? `${BASE_URL}/api/data/desired-state?exchange=${encodeURIComponent(exchange)}`
      : `${BASE_URL}/api/data/desired-state`;
    return fetch(url, { signal }).then(asJson<DesiredStateReport>);
  },
};

export { DataApiError };
export type { AssetCatalogEntry };
