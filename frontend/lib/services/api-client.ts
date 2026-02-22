// T007 - Fetch-based API client with all endpoint methods

import type {
  BacktestRun,
  BacktestSubmission,
  BacktestStatus,
  PagedResponse,
  EquityPoint,
  EventsData,
  RunBacktestRequest,
  RunOptimizationRequest,
  OptimizationRun,
  OptimizationSubmission,
  OptimizationStatus,
  StartDebugSessionRequest,
  DebugSession,
  DebugSessionStatus,
} from "@/types/api";

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

// ---------------------------------------------------------------------------
// Filter param interfaces
// ---------------------------------------------------------------------------

export interface BacktestListParams {
  strategyName?: string;
  assetName?: string;
  exchange?: string;
  timeFrame?: string;
  standaloneOnly?: boolean;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

export interface OptimizationListParams {
  strategyName?: string;
  assetName?: string;
  exchange?: string;
  timeFrame?: string;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const DEFAULT_TIMEOUT_MS = 30_000;

function buildQueryString(
  params: Record<string, string | number | boolean | undefined>,
): string {
  const entries = Object.entries(params).filter(
    (entry): entry is [string, string | number | boolean] =>
      entry[1] !== undefined,
  );
  if (entries.length === 0) return "";
  const qs = entries
    .map(
      ([key, value]) =>
        `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`,
    )
    .join("&");
  return `?${qs}`;
}

function extractErrorMessage(body: unknown): string {
  if (
    typeof body === "object" &&
    body !== null &&
    "message" in body &&
    typeof (body as Record<string, unknown>).message === "string"
  ) {
    return (body as Record<string, string>).message;
  }
  return JSON.stringify(body);
}

async function handleErrorResponse(response: Response): Promise<never> {
  let errorMessage: string;
  try {
    const body: unknown = await response.json();
    errorMessage = extractErrorMessage(body);
  } catch {
    errorMessage = response.statusText;
  }
  throw new Error(`API error ${response.status}: ${errorMessage}`);
}

function fetchWithTimeout(
  url: string,
  init?: RequestInit,
): { promise: Promise<Response>; abort: () => void } {
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

  const promise = fetch(url, {
    ...init,
    signal: init?.signal ?? controller.signal,
  }).finally(() => clearTimeout(timeoutId));

  return { promise, abort: () => controller.abort() };
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const { promise } = fetchWithTimeout(`${BASE_URL}${path}`, init);
  const response = await promise;
  if (!response.ok) {
    await handleErrorResponse(response);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return response.json() as Promise<T>;
}

async function requestVoid(path: string, init?: RequestInit): Promise<void> {
  const { promise } = fetchWithTimeout(`${BASE_URL}${path}`, init);
  const response = await promise;
  if (!response.ok) {
    await handleErrorResponse(response);
  }
}

// ---------------------------------------------------------------------------
// API client
// ---------------------------------------------------------------------------

export const apiClient = {
  // --- Strategies ---

  getStrategies(): Promise<string[]> {
    return request<string[]>("/api/strategies");
  },

  // --- Backtests ---

  getBacktests(
    params?: BacktestListParams,
  ): Promise<PagedResponse<BacktestRun>> {
    const qs = buildQueryString({ ...params });
    return request<PagedResponse<BacktestRun>>(`/api/backtests${qs}`);
  },

  getBacktest(id: string): Promise<BacktestRun> {
    return request<BacktestRun>(`/api/backtests/${encodeURIComponent(id)}`);
  },

  getBacktestEquity(id: string): Promise<EquityPoint[]> {
    return request<EquityPoint[]>(
      `/api/backtests/${encodeURIComponent(id)}/equity`,
    );
  },

  getBacktestEvents(id: string): Promise<EventsData> {
    return request<EventsData>(
      `/api/backtests/${encodeURIComponent(id)}/events`,
    );
  },

  runBacktest(req: RunBacktestRequest): Promise<BacktestSubmission> {
    return request<BacktestSubmission>("/api/backtests", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(req),
    });
  },

  getBacktestStatus(id: string): Promise<BacktestStatus> {
    return request<BacktestStatus>(
      `/api/backtests/${encodeURIComponent(id)}/status`,
    );
  },

  cancelBacktest(id: string): Promise<{ id: string; status: string }> {
    return request<{ id: string; status: string }>(
      `/api/backtests/${encodeURIComponent(id)}/cancel`,
      { method: "POST" },
    );
  },

  // --- Optimizations ---

  getOptimizations(
    params?: OptimizationListParams,
  ): Promise<PagedResponse<OptimizationRun>> {
    const qs = buildQueryString({ ...params });
    return request<PagedResponse<OptimizationRun>>(
      `/api/optimizations${qs}`,
    );
  },

  getOptimization(id: string): Promise<OptimizationRun> {
    return request<OptimizationRun>(
      `/api/optimizations/${encodeURIComponent(id)}`,
    );
  },

  runOptimization(
    req: RunOptimizationRequest,
  ): Promise<OptimizationSubmission> {
    return request<OptimizationSubmission>("/api/optimizations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(req),
    });
  },

  getOptimizationStatus(id: string): Promise<OptimizationStatus> {
    return request<OptimizationStatus>(
      `/api/optimizations/${encodeURIComponent(id)}/status`,
    );
  },

  cancelOptimization(id: string): Promise<{ id: string; status: string }> {
    return request<{ id: string; status: string }>(
      `/api/optimizations/${encodeURIComponent(id)}/cancel`,
      { method: "POST" },
    );
  },

  // --- Debug sessions ---

  createDebugSession(
    req: StartDebugSessionRequest,
  ): Promise<DebugSession> {
    return request<DebugSession>("/api/debug-sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(req),
    });
  },

  getDebugSession(id: string): Promise<DebugSessionStatus> {
    return request<DebugSessionStatus>(
      `/api/debug-sessions/${encodeURIComponent(id)}`,
    );
  },

  async deleteDebugSession(id: string): Promise<void> {
    await requestVoid(
      `/api/debug-sessions/${encodeURIComponent(id)}`,
      { method: "DELETE" },
    );
  },

  getDebugWebSocketUrl(sessionId: string): string {
    const url = new URL(BASE_URL);
    const wsProtocol = url.protocol === "https:" ? "wss:" : "ws:";
    return `${wsProtocol}//${url.host}/api/debug-sessions/${encodeURIComponent(sessionId)}/ws`;
  },
};
