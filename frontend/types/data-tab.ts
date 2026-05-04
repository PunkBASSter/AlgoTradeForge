// Phase 3 — Data tab types. Mirrors HistoryLoader §5 catalog/status/aggregation-options
// payloads on the wire. The main API proxies these byte-identical (P3-9 contract), so the
// FE consumes snake_case verbatim from upstream — DO NOT add a camelCase converter.

// ---------------------------------------------------------------------------
// Catalog (TRD §5.1)
// ---------------------------------------------------------------------------

export interface ExchangeListResponse {
  exchanges: ExchangeSummary[];
}

export interface ExchangeSummary {
  name: string;
  asset_count: number;
}

export interface AssetListResponse {
  assets: AssetCatalogEntry[];
}

export interface AssetCatalogEntry {
  exchange: string;
  // Directory name on disk (e.g. "BTCUSDT_perp"). Used as the URL path segment in
  // /api/data/exchanges/{exchange}/assets/{asset}/... endpoints — keep verbatim.
  symbol: string;
  // Human-readable label (e.g. "BTCUSDT"). Use this for any user-visible rendering.
  display_name: string;
  type: string;
  feeds: FeedCatalogEntry[];
}

export interface FeedCatalogEntry {
  id: string;
  kind: FeedKind;
  // Time bars carry interval ("1m", "5m", "1h", ...). Alt bars / ticks / side leave it null.
  interval: string | null;
  // Alt-bar fields surface for sorting (TRD §3.3 / column comparator).
  type_code: string | null;
  threshold_value: number | null;
  // Sidecar-bearing alt bars (EqI) carry a non-null sidecar pointer; the FE renders an
  // indicator dot for these (P3-14). null for non-sidecar feeds.
  sidecar: string | null;
}

export type FeedKind =
  | "OHLCV_TimeBar"
  | "OHLCV_AltBar"
  | "Tick"
  | "Side"
  | "aggregated";

// ---------------------------------------------------------------------------
// Per-feed status + eligibility (TRD §5.2 / §5.3)
// ---------------------------------------------------------------------------

export interface FeedStatusResponse {
  feed_id: string;
  // Verbatim feeds.json entry — opaque to the FE, rendered in CodeMirror (P3-15).
  definition: FeedDefinition;
}

export interface FeedDefinition {
  kind?: string;
  interval?: string;
  columns?: string[];
  // Aggregated alt-bar fields (TRD §4)
  type?: { code: string; name: string } | null;
  source?: { feed: string; record_count: number } | null;
  threshold?: ThresholdInfo | null;
  build?: BuildInfo | null;
  fidelity?: FidelityInfo | null;
  first_bar_ts?: string | null;
  last_bar_ts?: string | null;
  sidecar?: string | null;
  nullable_columns?: boolean | null;
  auto_apply?: { type: string; rate_column: string; sign_convention?: string | null } | null;
  // Forward-compatibility — unknown keys are tolerated by the JSON renderer.
  [key: string]: unknown;
}

export interface ThresholdInfo {
  value: number;
  unit: string;
  input_mode: "absolute" | "convenience";
  convenience_input: string | null;
}

export interface BuildInfo {
  tool_version: string;
  built_at: string;
  duration_seconds: number;
  bar_count: number;
  partitions_written: string[];
  max_partition_size_mb: number;
  monotonic_bumps?: number | null;
}

export interface FidelityInfo {
  estimated_overshoot_pct: number;
  actual_overshoot_pct: number;
  max_overshoot_pct: number;
  median_source_record_value: number;
  n_factor: number;
  imbalance_reconstruction_method: "tick_signed" | "m1_taker_buy_proxy" | null;
}

export interface AggregationOptionsResponse {
  feed_id: string;
  kind: string;
  eligible_types: string[];
  ineligible_types: { code: string; reason: string }[];
  threshold_bounds: { min: number; max: number | null };
  warnings: string[];
}

// ---------------------------------------------------------------------------
// Aggregate request + 202/423/409/422 responses (TRD §5.4)
// ---------------------------------------------------------------------------

export interface AggregateRequest {
  source_feed_id: string;
  type_code: string;
  // Threshold is null when input_mode == "convenience" (server parses convenience_input).
  threshold: number | null;
  threshold_unit: "base_asset" | "quote_asset" | "trades";
  input_mode: "absolute" | "convenience";
  convenience_input: string | null;
  overwrite_existing: boolean;
}

export interface AggregateAcceptedResponse {
  job_id: string;
  state: "queued";
}

export interface AggregateLockedResponse {
  code: "feed_already_locked";
  feed_id: string;
  existing_job_id: string;
  existing_job_state: "queued" | "running";
}

// ---------------------------------------------------------------------------
// Job snapshot + SSE event union (TRD §5.4)
// ---------------------------------------------------------------------------

export type JobState = "queued" | "running" | "completed" | "failed" | "cancelled";

export interface JobSnapshot {
  job_id: string;
  feed_id: string;
  state: JobState;
  queued_at: string;
  started_at: string | null;
  completed_at: string | null;
  queue_position?: number | null;
  current_partition?: string | null;
  bars_emitted?: number | null;
  summary?: JobSummary | null;
  error?: string | null;
}

export interface JobSummary {
  feed_id: string;
  bar_count: number;
  partitions_written: string[];
  fidelity: FidelityInfo;
  duration_seconds: number;
  sidecar_feed_id: string | null;
}

// SSE event payload union. The frame's `event:` field carries the discriminator; the FE
// parses `data:` JSON into the matching shape. Sequence ids are integers monotonically
// increasing within a single job (TRD §5.4); the FE persists the last seen id for resume.

export type SseEventType = "queued" | "started" | "progress" | "complete" | "error" | "cancelled";

export interface SseQueuedPayload {
  job_id: string;
  feed_id: string;
  queue_position: number;
}

export interface SseStartedPayload {
  job_id: string;
  feed_id: string;
  source_feed_id: string;
  started_at: string;
}

export interface SseProgressPayload {
  job_id: string;
  current_partition: string | null;
  bars_emitted: number;
  elapsed_ms: number;
}

export type SseCompletePayload = JobSummary & { job_id: string };

export interface SseErrorPayload {
  job_id: string;
  message: string;
}

// Phase 6 — emitted when a job is cancelled via DELETE /aggregations/{jobId}. Distinct from
// `error` so the UI can render "Cancelled" with the cancellation reason rather than a failure
// message. `reason` is "user_cancelled" today; future programmatic cancel paths may add others.
export interface SseCancelledPayload {
  job_id: string;
  reason: string;
  at_utc: string;
}

export type SseEventPayload =
  | SseQueuedPayload
  | SseStartedPayload
  | SseProgressPayload
  | SseCompletePayload
  | SseErrorPayload
  | SseCancelledPayload;
