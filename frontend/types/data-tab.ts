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
  source?: AggregatedSourceInfo | null;
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

export interface AggregatedSourceInfo {
  feed: string;
  record_count: number;
  first_ts?: string | null;
  /** Continue's no_new_data probe compares the source tail against this. */
  last_ts?: string | null;
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
  /** Renko resume anchor; null for non-Renko feeds. */
  last_brick_close?: number | null;
  /** Fresh = 1; +1 per continue. */
  run_count?: number | null;
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
}

export interface AggregateAcceptedResponse {
  job_id: string;
  state: "queued";
}

/** 200 from Continue when the source hasn't advanced; no job enqueued. */
export interface AggregateNoOpResponse {
  code: "no_new_data";
  feed_id: string;
  last_source_ts: number;
  last_bar_ts: string | null;
}

export type AggregateResponse = AggregateAcceptedResponse | AggregateNoOpResponse;

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

// ---- Archive coverage + load jobs (phase 2). Snake_case verbatim, same proxy rule. ----

export interface CoverageResponse {
  asset_dir: string;
  feeds: CoverageFeedEntry[];
}

export interface CoverageFeedEntry {
  feed_name: string;
  interval: string;
  covered_months: string[]; // "yyyy-MM", sorted ordinal
  first_timestamp: number | null; // epoch ms; null when no FeedStatus exists
  last_timestamp: number | null;
}

export interface LoadRequestBody {
  exchange: string;
  /** DISPLAY symbol ("BTCUSDT") — NOT the catalog directory name ("BTCUSDT_perp"). */
  symbol: string;
  asset_type: string;
  feed_name: string;
  interval: string;
  from: string; // "yyyy-MM-dd"
  to: string;
}

export interface LoadAcceptedResponse {
  job_id: string;
}

export type LoadJobStateWire = "queued" | "running" | "complete" | "error";

export interface LoadJobSnapshotWire {
  job_id: string;
  state: LoadJobStateWire;
  queued_at: string;
  completed_at: string | null;
  months_done: number;
  months_total: number;
  current_month: string | null;
  error_code: string | null;
  error_message: string | null;
  symbol: string;
  feed_name: string;
  interval: string;
  from: string;
  to: string;
}

// ---- Collection groups (declarative data management). ----

/** Wire shape for each entry in GET /api/data/groups. Snake_case verbatim. */
export interface CollectionGroupSummary {
  name: string;
  enabled: boolean;
  exchanges: string[];
  symbol_count: number;
  feed_count: number;
  etag: string;
}

/**
 * CollectionGroup document as returned by GET /api/data/groups/{name}.
 * camelCase (GroupJson serializer on the backend) — intentional divergence from
 * the snake_case list summary above.
 */
export interface CollectionGroupDoc {
  name: string;
  enabled: boolean;
  exchanges: string[];
  assets: {
    symbols: string[];
    historyStart: string;
  };
  feeds: Record<string, {
    collect: string;
    intervals?: string[] | null;
    format?: string | null;
  }>;
  derived?: Record<string, {
    source: string;
    type?: string | null;
    threshold?: string | null;
    sourceInterval?: string | null;
    materialize: string;
  }> | null;
  symbolOverrides?: Record<string, Record<string, string>> | null;
}

export interface ValidateExpansionUnsupported {
  exchange: string;
  canonical: string;
  reason: string;
}

export interface GroupConflict {
  key: string;
  kind: string;
  groups: string[];
  message: string;
}

export interface ValidateExpansionPerExchange {
  exchange: string;
  symbols: number;
  feeds: number;
}

/** Response from POST /api/data/groups/validate (200). 422 throws DataApiError. */
export interface ValidatePreview {
  errors: string[];
  expansion: {
    tuple_count: number;
    unsupported: ValidateExpansionUnsupported[];
    conflicts: GroupConflict[];
    per_exchange: ValidateExpansionPerExchange[];
    already_materialized: number;
  };
}

export type TupleStatusValue =
  | "unsupported"
  | "on-demand"
  | "missing"
  | "partial"
  | "materialized";

/** Single tuple entry in the desired-state report. */
export interface TupleStatus {
  exchange: string;
  canonical: string;
  dir: string | null;
  feed_name: string;
  interval: string;
  status: TupleStatusValue;
  months_expected: number;
  months_covered: number;
  collect: string;
  history_start: string | null;
  is_derived: boolean;
  groups: string[];
}

export interface DesiredStateOrphan {
  exchange: string;
  dir: string;
  feed_name: string;
  interval: string;
}

/** Response from GET /api/data/desired-state. */
export interface DesiredStateReport {
  computed_at: string;
  tuples: TupleStatus[];
  orphaned: DesiredStateOrphan[];
  orphaned_total: number;
  conflicts: GroupConflict[];
}
