// Data tab wire types. Proxied byte-identical from HistoryLoader, so the FE consumes
// snake_case verbatim — DO NOT add a camelCase converter.

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
  /** On-disk directory name (e.g. "BTCUSDT_perp"). Used verbatim in URL path segments. */
  symbol: string;
  /** Human-readable label (e.g. "BTCUSDT") for user-visible rendering. */
  display_name: string;
  type: string;
  feeds: FeedCatalogEntry[];
}

export interface FeedCatalogEntry {
  id: string;
  kind: FeedKind;
  /** Time bars only ("1m", "5m", ...). Alt bars / ticks / side are null. */
  interval: string | null;
  type_code: string | null;
  threshold_value: number | null;
  /** Non-null for sidecar-bearing alt bars (EqIV); null otherwise. */
  sidecar: string | null;
}

export type FeedKind =
  | "OHLCV_TimeBar"
  | "OHLCV_AltBar"
  | "Tick"
  | "Side"
  | "aggregated";

export interface FeedStatusResponse {
  feed_id: string;
  /** Verbatim feeds.json entry; rendered as JSON, opaque to the FE. */
  definition: FeedDefinition;
}

export interface FeedDefinition {
  kind?: string;
  interval?: string;
  columns?: string[];
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
  /** Forward-compatibility: unknown keys flow through to the JSON renderer. */
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
  /** Strictly out-of-order tick records the source decorator recovered from. Distinct
   *  from monotonic_bumps (benign equal-ms clustering); non-zero implies an upstream
   *  defect. */
  monotonic_regressions?: number | null;
}

export interface FidelityInfo {
  estimated_overshoot_pct: number;
  actual_overshoot_pct: number;
  max_overshoot_pct: number;
  median_source_record_value: number;
  n_factor: number;
  imbalance_reconstruction_method:
    | "tick_signed"             // EqIV from tick source
    | "m1_taker_buy_proxy"      // EqIV from time-bar source
    | "tick_signed_dollar"      // EqID from tick source
    | "m1_taker_buy_quote_proxy"// EqID from time-bar source
    | "tick_signed_count"       // EqIT from tick source
    | "m1_taker_buy_count_proxy"// EqIT from time-bar source (double proxy: count itself derived from taker_buy_vol ratio at ingest time)
    | null;                     // Non-imbalance feeds (EqV, EqT, EqD, Range, Renko)
}

export interface AggregationOptionsResponse {
  feed_id: string;
  kind: string;
  eligible_types: string[];
  ineligible_types: { code: string; reason: string }[];
  threshold_bounds: { min: number; max: number | null };
  warnings: string[];
}

export interface AggregateRequest {
  source_feed_id: string;
  type_code: string;
  /** Null when input_mode == "convenience" (server parses convenience_input). */
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

// Sequence ids are integers monotonically increasing within a single job; the FE
// persists the last seen id for resume.

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

// Distinct from `error` so the UI can render "Cancelled" with reason rather than a
// failure message. `reason` is "user_cancelled" today.
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

// Discriminated union — narrow `data` automatically off `env.type`. The sole
// `JSON.parse → as` cast lives in data-sse-client.ts inside the type switch, so server
// drift surfaces in one place.
export type SseEventEnvelope =
  | { type: "queued"; data: SseQueuedPayload }
  | { type: "started"; data: SseStartedPayload }
  | { type: "progress"; data: SseProgressPayload }
  | { type: "complete"; data: SseCompletePayload }
  | { type: "error"; data: SseErrorPayload }
  | { type: "cancelled"; data: SseCancelledPayload };
