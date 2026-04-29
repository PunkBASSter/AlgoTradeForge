# TRD: Alternative Bar Aggregations in AlgoTradeForge

**Status:** Draft v5
**Scope:** HistoryLoader + AlgoTradeForge main API + Web UI

---

## 1. Overview

Add information-driven bars (`EqT`/`EqV`/`EqD`/`EqI`) and reserve a slot for path-dependent bars (Range, Renko). Aggregated feeds are persisted from existing HistoryLoader data (`1m`/`5m`/`1h`/`1d` OHLCV or raw ticks), exposed via REST, surfaced in a new **Data** tab, and selectable as primary OHLC for backtest/optimization/validation/debug runs.

`DataSubscription` is replaced cleanly — no back-compat shims.

**Goals:** persisted reusable feeds, memory-bounded streaming aggregation, scalable storage convention, fidelity metadata per feed.
**Non-goals (v1):** live tick streaming aggregation, Range/Renko, cross-exchange composites, migration of existing artifacts.

## 2. Glossary

| Term | Meaning |
|---|---|
| Source feed | Persisted input: `1m`, `5m`, `1h`, `ticks`, … |
| Aggregated feed | Persisted output: an alt-bar series. |
| Threshold (N) | Aggregation parameter (volume/ticks/dollars/imbalance per bar). |
| Overshoot | Per-bar excess over N due to source granularity. |
| Bar type code | `EqT`, `EqV`, `EqD`, `EqI`, future `Range`, `Renko`. |
| Feed ID | Stable identifier within `(exchange, asset)`; URL path component, partition dir name. |

## 3. Storage Convention

### 3.1 Layout

```
History/binance/BTCUSDT_perp/
├── candles/2019-09_1m.csv, 2019-09_1h.csv, …          # existing
├── ticks/2024-01-01.csv, …                            # daily, §3.5
├── funding-rate/2019-09_8h.csv                        # existing side feed
├── aggregated/
│   ├── EqV_1m_1000/2019-09.csv, …
│   ├── EqI_ticks_500000/2019-09.csv, …
│   └── EqI_ticks_500000.flow/2019-09.csv, …           # sidecar FeedSeries (sibling of bar dir, under aggregated/)
└── feeds.json
```

Bars land in the partition matching UTC `YYYY-MM` of `ts_open`. Multi-month bars (large N, low activity) land in the open-month file; intervening months produce **no file**. Readers discover files via the §3.2 glob (`aggregated/<feedId>/*.csv`); `build.partitions_written` is a coverage summary (set of `YYYY-MM` strings present), not an authoritative file list — file-level discovery is glob-driven, calendar-level coverage is manifest-driven.

### 3.2 Part-numbered overflow

Soft byte budget (`aggregator.maxPartitionSizeMB`, default **100 MB**). On overflow, the month rolls into `<YYYY>-<MM>.p<NN>.csv` (zero-padded). Sequential numbering, no calendar coupling. Sticky for the rest of the month — a month is either single-file or part-numbered, never mixed.

**Mid-month first overflow:** close in-progress `<YYYY>-<MM>.csv` → atomic-rename to `.p01.csv` → open `.p02.csv`. Months following an overflow pre-open as `.p01.csv` from the first bar.

Reader globs `aggregated/<feedId>/*.csv` (sidecars use the analogous `aggregated/<feedId>.flow/*.csv` glob); zero-padding keeps lex sort = chronological (`2026-04.csv` < `2026-05.p01.csv` < `2026-05.p02.csv`). `partitions_written` in `feeds.json` records calendar coverage (months touched) and is **not** used for file enumeration — the glob is authoritative for which files to read.

**Cross-volume rename hazard.** Staging dirs (`.staging-<jobId>/`, §4.1) and `*.tmp` files live on the same volume as their target `aggregated/<feedId>/`. Cross-volume moves are not atomic on NTFS — co-locating them keeps every rename in-volume.

### 3.3 Naming grammar

```
aggregated/<TypeCode>_<SourceCode>_<Threshold>/<YYYY>-<MM>[.p<NN>].csv          # bars
aggregated/<TypeCode>_<SourceCode>_<Threshold>.flow/<YYYY>-<MM>[.p<NN>].csv     # sidecar (sibling dir of bars)
```

| Field | Values |
|---|---|
| `TypeCode` | `EqT`, `EqV`, `EqD`, `EqI`, future `Range`, `Renko` |
| `SourceCode` | `1m`, `5m`, `15m`, `1h`, `4h`, `1d`, `ticks` |
| `Threshold` | positive integer in **canonical display units** (the value the user enters); sub-unit values use the milli (`m`) / micro (`u`) suffixes — see §3.4 |

Display name uses SI: `EqV_1m_1000` ↔ `EqV/1m:1k`. Sub-unit thresholds: `EqV_1m_500m` ↔ `EqV/1m:500m` = 0.5 BTC base. Filename keeps an integer-with-suffix form (no `.`) so lex sort and path semantics are unaffected.

### 3.4 Threshold units & scale alignment

| TypeCode | Threshold meaning | Unit |
|---|---|---|
| EqT | tick count | trades |
| EqV | base-asset volume | base units |
| EqD | quote-asset volume | quote units |
| EqI | abs cumulative signed quote volume | quote units |

**Two representations of every threshold:**

| Form | Where | Units |
|---|---|---|
| Display | FeedId, filename, API, `feeds.json`, UI | human canonical (`1000` for 1000 BTC, `500m` for 0.5 BTC) |
| Scaled | accumulator runtime only | `ScaleContext.QuantityScale` (base) or `PriceScale` (quote) |

**Sub-unit thresholds.** The display form accepts SI suffixes both upward (`k`, `M`, `G`) and downward (`m` = 1e−3, `u` = 1e−6). The wire payload (`threshold` in the §5.4 request, `threshold.value` in the manifest) carries the **integer mantissa** and an explicit suffix (or unit-tag) so no `.`-bearing string ever lands in a filename or feedId. Server resolves `(mantissa, suffix) → scaled long` once at accumulator construction. Minimum effective threshold is `1u` of the canonical unit; below that the eligibility endpoint rejects the request.

Application layer applies `scale.AmountToTicks(displayThreshold)` once at accumulator construction. FeedId/filename/manifest never carry scaled longs. Scale-tag equality is asserted at accumulator entry **for primary-bar inputs**. Side feeds are unscaled `double` (§3.6); the aggregator handles `double → long` conversion explicitly at the sum site. Misalignment on a primary-bar input is a write-time validation failure. Storage is `long` (qty/price on primary bars) + `double` (side feeds and analytical sidecar); no `decimal` anywhere.

`ScaleContext.QuantityScale` is a Phase 1a deliverable on `ScaleContext` / `Asset`.

### 3.5 CSV schemas

**Candle / aggregated bar (identical):**
```
ts, o, h, l, c, vol           # all long; ts = epoch ms
```
- `ts` = bar **open** time. Bar duration is variable on alt bars.
- `vol ≥ 0` always. Sign-of-volume direction encoding rejected; flow data lives in sidecar.

The shared schema means `PartitionedCsvBarLoader` serves both with only path-resolution branching.

**Sidecar (`<feedId>.flow/<YYYY>-<MM>.csv`):**
```
ts, signed_imbalance, buy_volume, sell_volume, realized_threshold
```

| Column | CSV encoding | Read-API | Notes |
|---|---|---|---|
| `ts` | int (epoch ms) | `long` | Joins 1:1 to bar `ts`. |
| `signed_imbalance` | double | `double` | EqI only; empty ⇒ `NaN`. |
| `buy_volume` / `sell_volume` | double (or empty) | `double` | Raw base-asset units, side-feed convention (§3.6). From `taker_buy_*` (time-bar) or `is_buyer_maker` (tick). Empty ⇒ `NaN`. |
| `realized_threshold` | double | `double` | Accumulator value at close (≥ N; `abs(.) ≥ N` for EqI). Never empty. |

Sidecar columns are `double` on disk per the side-feed convention (§3.6). Per-bar values are bounded by realistic exchange magnitudes — well within 2^53. Cumulative aggregation across bars is the consumer's responsibility (downstream cumulants must guard the 2^53 boundary themselves).

EqI feeds MUST publish a sidecar (≥ `signed_imbalance`, `realized_threshold`). Other types MAY publish when source provides taker-buy split. Sidecar's `feeds.json` entry sets `nullable_columns: true` to opt into empty-cell→`NaN` parsing.

**Tick storage (Phase 2 prerequisite):**
```
History/.../ticks/<YYYY>-<MM>-<DD>.csv      # daily; ~50 MB / ~30 files per month
ts, price, qty, is_buyer_maker, agg_id
```
All `long` except `is_buyer_maker` (`int 0/1`). `is_buyer_maker=1` → sell-aggressor (EqI: −qty); `=0` → buy-aggressor (+qty). `agg_id` is the **ingestor**'s resume-on-crash key (last-written Binance trade ID per day-partition, used to de-dupe on reconnect). Aggregator runs are full-range in v1 (§5.4) and restart from scratch on crash; the §4.1 sweep deletes staging dirs so no partial bars survive.

### 3.6 Numeric type convention

Two type bands; one conversion boundary.

| Band | Storage on disk | Examples | Scaling |
|---|---|---|---|
| **Primary bars** | `long` | `candles/*.csv`, `aggregated/<feedId>/*.csv`, `ticks/*.csv` | Scaled by `feeds.json` `decimalDigits` / `ScaleContext.QuantityScale` |
| **Side feeds** | `double` | `candle-ext/*.csv`, `funding-rate/*.csv`, `*.flow/*.csv` (alt-bar sidecars), other auxiliary feeds | None — raw exchange units |

**Conversion boundary: aggregator entry.** When an aggregator joins primary-bar input (long) with side-feed input (double — e.g., `candle-ext.taker_buy_vol`), it converts at the sum site:
```
taker_buy_ticks = MoneyConvert.ToLong(taker_buy_vol_double * QuantityScale);
```
Aggregator-emitted bar `vol` is `long` (primary band); aggregator-emitted sidecar columns are `double` (side band). No code outside the aggregator boundary owns this conversion. Side feeds carry no scale tag in the manifest — the convention is universal.

**Why doubles for side feeds.** `candle-ext` and other auxiliary feeds already write doubles (raw Binance JSON values). Per-bar magnitudes are bounded by realistic exchange volumes and stay well within 2^53. Forcing scaled longs here would require a writer migration + backfill across every existing partition without buying any precision the strategy layer needs.

## 4. Feed Metadata (`feeds.json`)

Single per-asset manifest extends the existing `feeds.json`:

```json
{
  "feeds": {
    "EqV_1m_1000": {
      "kind": "aggregated",
      "type": { "code": "EqV", "name": "EqualVolume" },
      "source": { "feed": "1m", "first_ts": "...", "last_ts": "...", "record_count": 3458880 },
      "threshold": { "value": 1000, "unit": "base_asset", "input_mode": "absolute", "convenience_input": null },
      "build": {
        "tool_version": "1.4.0", "built_at": "...", "duration_seconds": 38,
        "bar_count": 18421, "partitions_written": ["2019-09", "..."],
        "max_partition_size_mb": 100
      },
      "fidelity": {
        "estimated_overshoot_pct": 2.5, "actual_overshoot_pct": 2.42,
        "max_overshoot_pct": 18.7, "median_source_record_value": 0.5,
        "n_factor": 2000,
        "imbalance_reconstruction_method": null    // MUST be present even on non-EqI feeds (§4 rule)
      },
      "first_bar_ts": "...", "last_bar_ts": "...", "sidecar": null
    },
    "EqI_ticks_500000.flow": {
      "kind": "side", "nullable_columns": true,
      "columns": ["signed_imbalance", "buy_volume", "sell_volume", "realized_threshold"]
    }
  }
}
```

- `kind: "aggregated"` → bar loader. `kind: "side"` (incl. `.flow`) → `CsvFeedSeriesLoader`.
- `build.partitions_written` records calendar coverage (months touched). File enumeration is glob-driven (§3.2); this field is for diagnostics, summary endpoints, and SSE `complete` payloads — not for read-path file discovery.
- `sidecar` → companion FeedSeries feed-id (auto-bound, §9.4); `null` if none.
- `fidelity.imbalance_reconstruction_method` ∈ `{ "tick_signed", "m1_taker_buy_proxy", null }`. Time-bar EqI → proxy + UI warning. Non-EqI feeds set this to `null` **explicitly** (the field MUST be present); absence indicates a malformed manifest.
- `estimated_overshoot_pct = 100 / (2 × n_factor)`, `n_factor = threshold / median_source_record_value`. For tick sources, expect `actual_overshoot_pct ≤ 0.05%`.

### 4.1 Atomicity & cleanup

The presence of the `feeds.json` entry is the "feed complete" marker. Writer rule: partitions first, manifest last via write-temp-then-rename.

**Crash mid-write.** Startup sweep on every HistoryLoader boot:
- Delete every `*.tmp` under any `aggregated/<feedId>/` or `aggregated/<feedId>.flow/`.
- Recursively delete any `aggregated/<feedId>/` or `aggregated/<feedId>.flow/` directory whose `feedId` is absent from `feeds.json`. Each deletion is logged at WARN with the absolute path so manually-staged test data is recoverable from logs after the fact.

**Overwrite (`overwrite_existing=true`).** Stage to `aggregated/<feedId>/.staging-<jobId>/`, then atomic rename of staging → live (deleting old live first), then write `feeds.json`. Interrupted rename is cleaned by the next startup sweep.

**Concurrent manifest writes.** Manifest writer is internally serialized per `(exchange, asset)` `feeds.json` path: shared lock for readers, exclusive lock for writers. Different `feed_id`s on the same asset can aggregate in parallel; only manifest mutation is serialized. The writer protocol is a single-lock read-merge-write — re-reading the manifest under the same exclusive lock is what guarantees parallel writers don't lose each other's entries:

```
acquire_exclusive_lock(feeds.json)
  manifest = read_current(feeds.json)        # under exclusive lock, not shared
  manifest.feeds[feed_id] = own_entry
  write_temp_file(manifest)
  atomic_rename(temp → feeds.json)
release_exclusive_lock
```

## 5. HistoryLoader REST API

All endpoints are short-lived. Aggregation is **always async via the §6.5 job queue** — no sync POST, no `Prefer: wait=N`, no long-held connections. POST returns 202 on enqueue; FE consumes progress via SSE.

### 5.1 Discovery

```
GET /api/v1/exchanges                                → exchanges + asset counts
GET /api/v1/exchanges/{exchange}/assets              → assets + feed list (one exchange)
GET /api/v1/assets                                   → catalog: all assets across all exchanges
```

`GET /api/v1/assets` is the FE Data-tab's single load:

```json
{
  "assets": [{
    "exchange": "binance", "asset": "BTCUSDT_perp",
    "asset_class": "CryptoPerpetualAsset",
    "feeds": [
      { "id": "1m", "display_name": "1m", "kind": "OHLCV_TimeBar",
        "size": 3458880, "first_ts": "...", "last_ts": "...", "sidecar": null },
      { "id": "EqV_1m_1000", "display_name": "EqV/1m:1k", "kind": "OHLCV_AltBar",
        "size": 18421, "first_ts": "...", "last_ts": "...", "sidecar": null }
    ]
  }],
  "generated_at": "..."
}
```

`feeds[].kind` ∈ `{ OHLCV_TimeBar, OHLCV_AltBar, Tick, Side }`. Job-eligibility is **not** a catalog property — see §5.3.

**Caching.** Both per-exchange and cross-exchange endpoints serve from an in-memory snapshot rebuilt on `feeds.json` write (manifest writer raises an event). 30 s TTL fallback. Aggregate/delete invalidates both per-asset and catalog keys.

### 5.2 Feed inspection

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status
```
Returns the `feeds.json` entry verbatim for aggregated/side; partition-derived minimal status for time bars / ticks.

### 5.3 Aggregation eligibility

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options
```

Drives the FE form (Type dropdown, N validator, warning banners, button-enabled state) from one payload:

```json
{
  "source": { "feed_id": "1m", "kind": "OHLCV_TimeBar", "record_count": 3458880, "has_candle_ext": true },
  "eligible_types": [
    { "code": "EqV", "display_name": "Equal Volume",
      "threshold_unit": "base_asset",
      "threshold_min": 1, "threshold_max": 1000000000, "threshold_default": 1000,
      "format_hint": "base-asset units; SI suffixes 'k', 'M' accepted",
      "warnings": [] },
    { "code": "EqI", "display_name": "Equal Imbalance",
      "threshold_unit": "quote_asset",
      "threshold_min": 1000, "threshold_max": 100000000000, "threshold_default": 500000,
      "warnings": [
        { "code": "imbalance_proxy",
          "message": "Time-bar EqI uses the taker-buy proxy; rebuild from `ticks` for magnitude-sensitive use." }] }
  ],
  "ineligible_reason": null
}
```

When the source is not aggregatable: `eligible_types: []`, `ineligible_reason: { code, message }` (FE renders as disabled-button tooltip).

**Caching.** Eligibility responses cache on the same invalidation event as the `/api/v1/assets` catalog (manifest writer raises an event; both keys clear together). All `feeds.json` writes — alt-bar aggregation **and** existing HistoryLoader registrations (`candle-ext`, `funding-rate`, etc.) — flow through the §4.1 synchronized writer, so a fresh `candle-ext` registration invalidates eligibility caches that depend on `has_candle_ext` without a separate code path.

### 5.4 Aggregation command

```
POST /api/v1/exchanges/{exchange}/assets/{asset}/aggregate
Body: {
  source_feed_id, type_code,
  threshold,                              // numeric value in canonical display units when input_mode=absolute
  threshold_unit,                         // "base_asset" | "quote_asset" | "trades"
  input_mode,                             // "absolute" | "convenience"; see Phase 0 resolution
  convenience_input,                      // original user input (e.g. "1k 1m"); echoed into feeds.json. Required when input_mode=convenience, else null
  overwrite_existing
}

→ 202 { "jobId": "...", "state": "queued|running" }
  Location: /api/v1/aggregations/{jobId}/progress, X-Job-Id: ...

GET /api/v1/aggregations/{jobId}/progress       → SSE stream
GET /api/v1/aggregations/{jobId}                → snapshot
```

v1 is full-range only; partial / resumable aggregation (`from_ts`/`to_ts`) is deferred to Phase 6 (§12 item 3).

Snapshot:
```json
{ "job_id": "...", "feed_id": "EqV_1m_1000",
  "state": "queued|running|complete|error",
  "queued_at": "...", "started_at": "...", "completed_at": "...",
  "queue_position": 2,                  // when state=queued
  "current_partition": "2024-03",       // when state=running
  "fraction_complete": 0.36,            // when state=running
  "summary": { ... },                   // when state=complete; identical shape to the SSE `complete` event payload (minus `type`/`job_id`)
  "error": { "code": "...", "message": "..." } }
```

**SSE events.** Order: `queued? → started → progress* → (complete | error)`. Stream closes after terminal event.

```
event: queued      → { type, job_id, feed_id, queued_at, queue_position }
event: started     → { type, job_id, feed_id, started_at, source_feed_id, source_record_count, estimated_bar_count }
event: progress    → { type, job_id, current_partition, partitions_written_count, bars_emitted, elapsed_ms, fraction_complete }
event: complete    → { type, job_id, feed_id, feeds_json_path, sidecar_feed_id,
                       fidelity: { actual_overshoot_pct, max_overshoot_pct, estimated_overshoot_pct },
                       duration_seconds, bar_count,
                       partitions_written: ["YYYY-MM", ...],
                       partitions_written_count }
event: error       → { type, job_id, code, message, retryable }
```

The `complete` payload is the canonical summary (also returned under `summary` in the snapshot once `state=complete`). `partitions_written_count` (in `progress`) is an integer running counter; `partitions_written` (in `complete` and §4 manifest) is the array of `YYYY-MM` strings — distinct names for distinct shapes.

**Cancellation.** Out of scope in v1. To abort an in-flight job, restart HistoryLoader; the §4.1 startup sweep cleans staging dirs and orphan partitions, so restart is always safe (no half-built feeds visible to readers). Restart aborts **all** concurrent jobs, not just the targeted one — the FE Data tab should label these as "interrupted" rather than "failed" in v1, so users can distinguish self-cancellation from a host restart. A future `DELETE /aggregations/{jobId}` is reserved for Phase 6 alongside the durable queue.

**Reconnect.** Standard SSE `Last-Event-ID` resumes from next event. Without it, server emits synthetic `started + progress` snapshot then resumes live. Terminal event retained 15 min; thereafter `progress` returns 410 Gone and FE falls back to the snapshot endpoint. FE persists `jobId` in `localStorage` keyed by `(exchange, asset, feedId)`.

**Errors.**
- `409` — feed exists, `overwrite_existing=false`.
- `422` — type incompatible with source.
- `423` — duplicate outcome `feed_id` already queued/running. Body: `{ code: "feed_already_locked", feed_id, existing_job_id, existing_job_state }`. FE attaches to the existing job's progress instead of error-toasting.
- `503` — queue full (rare; `Retry-After` header).

**Status-code precedence:** `423` (active job) is checked before `409` (feed exists). When `overwrite_existing=true` is mid-run, the existing manifest entry still exists on disk while staging is in flight — a duplicate enqueue must surface as the active-job conflict, not the on-disk-feed conflict, so the FE can attach to the in-flight progress stream. `422` (type/source incompatibility) is a request-validation error and runs before either.

### 5.5 Feed deletion

```
DELETE /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}
```
Allowed only for `OHLCV_AltBar`. Time bars / ticks / side feeds → 403.

### 5.6 FE flow → endpoint coverage

Sketch: `docs/alternative-bars-ui.png`.

| FE interaction | Endpoint |
|---|---|
| Mount Data tab; render full grid | `GET /api/v1/assets` (one call) |
| Click `+` cell → Status JSON | `GET .../feeds/{feedId}/status` |
| Click `+` cell → form (Type/N/warnings/button-enabled) | `GET .../feeds/{feedId}/aggregation-options` |
| Aggregate click → enqueue + progress loader | `POST .../aggregate` (202) → SSE `progress` |
| Same-feedId double-click | `POST .../aggregate` → 423 with `existing_job_id`; FE attaches to existing SSE |
| FE refresh during aggregation | `GET .../aggregations/{jobId}/progress` (`Last-Event-ID` from `localStorage`) |
| Post-success refresh of grid | catalog cache invalidates on success; FE re-fetches `/api/v1/assets` |
| Delete | `DELETE .../feeds/{feedId}` |

## 6. Aggregation Pipeline (HistoryLoader internals)

### 6.1 Wall-time envelope

| Source | Scale | Records | Wall time |
|---|---|---|---|
| `1m` | 1 sym × 5 y | 2.6 M | seconds |
| `1m` | 50 sym × 5 y (sequential) | 130 M | minutes |
| `ticks` | BTCUSDT × 1 y | 100 M | minutes |
| `ticks` | BTCUSDT × 5 y | 500 M | tens of minutes |

### 6.2 Streaming aggregator

```
PartitionedSourceReader → BarAccumulator → PartitionedSinkWriter → FeedsJsonFinalizer
```

- **Source reader:** chronological enumeration across partition boundaries. EqI from time bars joins `candle-ext` 1:1 by `ts` (perp/future only — spot has no `candle-ext`, EqI rejected at eligibility). Tick sources have intrinsic `is_buyer_maker`, never join.
- **Partial coverage:** for joined-source types (EqI from time bars), bars outside `candles/` ∩ `candle-ext/` are not emitted. Non-joined types (EqT/EqV/EqD from time bars, any type from ticks) use the source feed's range alone. Resolved range surfaced in the §5.4 summary.
- **Memory:** O(1) accumulator, O(write_buffer) per partition.
- **Pipelined** via `System.Threading.Channels`. **Batched I/O:** 10k-row reads, 5k-bar flushes.
- **Atomic writes:** `*.tmp` then rename per partition; `feeds.json` last.
- **Size overflow:** `<YYYY>-<MM>.p<NN>.csv` per §3.2.

```csharp
public interface IBarAccumulator
{
    bool TryAdvance(in SourceRecord r, out AggregatedBar emitted);
    AggregationStats Finalize();
}
```

### 6.3 Per-type accumulators

| Type | State | Emission |
|---|---|---|
| EqT | tick counter, OHLC | `counter ≥ N` |
| EqV | base-vol acc (long), OHLC | `base_acc ≥ N` |
| EqD | quote-vol acc (long), OHLC | `quote_acc ≥ N` |
| EqI | signed acc (long; `is_buyer_maker` from ticks, `taker_buy` proxy from time bars), OHLC | `abs(signed_acc) ≥ N` |

OHLC: time-bar source — first-open / max-high / min-low / last-close, where "first" and "last" are determined by the source reader's **chronological** enumeration across partition boundaries (§6.2), not file/iterator order. Bar `vol` is the sum of source `vol` (long+long, no conversion). Buy/sell columns originate from `candle-ext.taker_buy_vol` (`double`, side-feed convention §3.6); the aggregator converts at the sum site:
```
taker_buy_ticks = MoneyConvert.ToLong(taker_buy_vol_double * QuantityScale);
signed_acc += (2 * taker_buy_ticks - source_vol_long);   // EqI proxy
```
Sidecar columns the aggregator emits (`buy_volume`, `sell_volume`, `signed_imbalance`, `realized_threshold`) are written as `double` per §3.6.

**Tick source.** OHLC derived per-tick. Strict monotonicity: when two ticks arrive with identical exchange-stamped milliseconds, the emitted bar enforces `bar.ts_open = max(prev_bar.ts_open + 1, raw_ts)` to honor `Int64Bar.TimestampMs` strict monotonicity (§9.4). The +1 ms bump is recorded in the aggregator stats so degenerate clusters surface in fidelity reporting.

### 6.4 Overshoot

`overshoot_pct_i = (realized_threshold_i − N) / N × 100`. Track running mean and max; persist both alongside the analytic estimate.

### 6.5 Job queue & concurrency

`POST .../aggregate` enqueues — workers drain.

| Component | Role |
|---|---|
| `IAggregationJobQueue` | Bounded `Channel<AggregationJob>`, capacity from `aggregator.maxQueueDepth` (default 64). |
| `AggregationWorkerHost` | `BackgroundService` running `aggregator.maxConcurrentJobs` workers (default 2). Each runs §6.2 pipeline serially. |
| `IAggregationJobRegistry` | `ConcurrentDictionary` dual-keyed by `jobId` and outcome `feed_id`. Holds state, timestamps, progress, `Channel<ProgressEvent>` for SSE. Terminal-state retained 15 min. |

**Concurrency rules:**
- Distinct outcome `feed_id`s run in parallel up to `maxConcurrentJobs` (e.g. `EqV_1m_1000` ‖ `EqV_1m_2000`). Different assets always parallel.
- Same outcome `feed_id` rejected at enqueue with **423** **only when the existing entry is in `queued` or `running` state**. Terminal entries (`complete`/`error`) do not block enqueue: a fresh enqueue evicts the terminal record (its 15-min retention is preempted) and replaces it with the new job. This avoids the "423 pointing to a finished job" UX trap during the retention window. Queue itself never holds duplicates of an active `feed_id`. FE attaches to the existing job via `existing_job_id` only on the active-state 423.
- §4.1 manifest exclusive lock still serializes the final `feeds.json` rewrite. Partition writes are independent (different feedIds → different dirs).
- All SSE on the host's standard Kestrel listener — chunked transfer-encoding, no timeout overrides.

**Restart semantics (v1, in-memory only):**
- In-flight job mid-write → §4.1 startup sweep cleans staging/orphans. Manifest never written → catalog reflects "feed missing" — consistent.
- Queued-not-started jobs lost. FE's cached `jobId` returns 404; FE clears it and prompts re-submit.
- Durable queue is Phase 6.

**Config (`aggregator.*`):** `maxConcurrentJobs=2`, `maxQueueDepth=64`, `jobRetentionMinutes=15`, `maxPartitionSizeMB=100`. Tuning via `AggregatorBenchmarks` (Phase 1b gate). Past 2–3 workers, shared-disk I/O contention dominates.

**Tick-source concurrency.** The Phase 1b benchmarks (`EqV_1m_1000`, `EqT_1m_500`) exercise time-bar sources only. Tick aggregation (Phase 2a+) is materially more I/O-bound (5y BTCUSDT ≈ 500M records, §6.1) and should run effectively serially: Phase 2a adds a tick-source benchmark scenario and re-tunes `maxConcurrentJobs` accordingly (likely a separate `aggregator.maxConcurrentTickJobs=1` gate rather than lowering the global default and starving time-bar throughput).

## 7. Compatibility Matrix

`candle-ext` (`HistoryLoader.Domain/FeedNames.cs:6`) is futures-only (`BinanceFuturesClient.CandleExtColumns`); spot has none.

| Source kind | Asset class | Eligible | Notes |
|---|---|---|---|
| Tick | any | EqT, EqV, EqD, EqI, future Range/Renko | Highest fidelity. |
| OHLCV_TimeBar **+ candle-ext** | Perp/Future | EqT, EqV, EqD, EqI* | EqI* = `m1_taker_buy_proxy`; UI warning. |
| OHLCV_TimeBar (no candle-ext) | Spot | EqT, EqV, EqD | EqI requires tick source. |
| OHLCV_TimeBar OHLC-only | any | future Range/Renko | No volume → volume types unbuildable. |
| OHLCV_AltBar | any | none | No re-aggregation in v1; deferred to Phase 6 (§12 item 5). |
| Side | any | none | Aggregate disabled. |

## 8. Main API (proxy layer)

All HistoryLoader endpoints (§5) mirrored under `/api/data/...` with identical contracts:
- Typed `HistoryLoaderClient` via `IHttpClientFactory` + `IOptions<HistoryLoaderOptions>{ BaseUrl, RequestTimeout }`.
- AuthN/AuthZ before forwarding.
- Caches `exchanges`, `exchanges/{e}/assets`, `assets` (5 s TTL). Event-driven invalidation: `POST aggregate` / `DELETE feed` clears affected `(exchange, asset)` and catalog keys.
- 5xx → `ProblemDetails` with stable error codes.
- Single `MapDataEndpoints()` extension.
- SSE pass-through (chunked transfer-encoding flows transparently). The pass-through route disables ASP.NET Core response buffering (`IHttpResponseBodyFeature.DisableBuffering()`) so chunks reach the FE without coalescing under load.

**FE invariant:** FE talks only to main `WebApi`, including in dev. Single CORS origin, single auth surface.

YARP is the upgrade path only when hosting multiple HistoryLoader instances.

## 9. AlgoTradeForge API: Subscription Redesign

`Application.DataSubscriptionDto` is replaced by a polymorphic hierarchy at the API/command boundary. The strategy-side `Domain.Strategy.DataSubscription` (carrying `Asset`) is a separate concern; sidecar interest uses a typed accessor (§9.4), not a magic feed key.

### 9.1 `TimeFrame` value type (Phase 1a)

```csharp
public readonly record struct TimeFrame(TimeSpan Duration)
{
    public string Code => TimeFrameFormatter.Format(Duration);
    public static TimeFrame Parse(string code) =>
        TimeFrameFormatter.TryParseShorthand(code, out var ts)
            ? new TimeFrame(ts)
            : throw new FormatException($"Invalid TimeFrame: '{code}'");
}
```

Wraps `TimeSpan`; raw-`TimeSpan` overloads removed in Phase 4.

### 9.2 Subscription hierarchy

```csharp
public abstract record DataFeedSubscription(string Exchange, string Asset)
{
    public abstract string FeedId { get; }
    public abstract DataFeedKind Kind { get; }
    public DataFeedRole Role { get; init; } = DataFeedRole.Side;
}

public enum DataFeedKind { TimeBar, AltBar, Tick, Side }
public enum DataFeedRole { Primary, Side }

public sealed record TimeBarSubscription(string Exchange, string Asset, TimeFrame TimeFrame)
    : DataFeedSubscription(Exchange, Asset)
{ public override string FeedId => TimeFrame.Code; public override DataFeedKind Kind => DataFeedKind.TimeBar; }

public sealed record AltBarSubscription(
    string Exchange, string Asset, AltBarType Type, string SourceFeedId, long Threshold)
    : DataFeedSubscription(Exchange, Asset)
{
    // Threshold = canonical display value (§3.4); Application applies scale.AmountToTicks once at accumulator construction.
    public override string FeedId => $"{Type.Code}_{SourceFeedId}_{Threshold}";
    public override DataFeedKind Kind => DataFeedKind.AltBar;
}

public sealed record TickSubscription(string Exchange, string Asset)
    : DataFeedSubscription(Exchange, Asset)
{ public override string FeedId => "ticks"; public override DataFeedKind Kind => DataFeedKind.Tick; }

public sealed record SideFeedSubscription(string Exchange, string Asset, string SideFeedId)
    : DataFeedSubscription(Exchange, Asset)
{ public override string FeedId => SideFeedId; public override DataFeedKind Kind => DataFeedKind.Side; }
```

### 9.3 Backtest input model

```csharp
public sealed class BacktestInputs
{
    public required DataFeedSubscription Primary { get; init; }   // Role=Primary, Kind ∈ {TimeBar, AltBar}
    public IReadOnlyList<DataFeedSubscription> SideFeeds { get; init; } = [];
    public DateRange Range { get; init; }
}
```

Engine resolves to glob:

| Kind | Glob |
|---|---|
| TimeBar | `<root>/{Exchange}/{Asset}/candles/*_{FeedId}.csv` |
| AltBar | `<root>/{Exchange}/{Asset}/aggregated/{FeedId}/*.csv` |
| Tick | `<root>/{Exchange}/{Asset}/ticks/*.csv` |
| Side (alt-bar sidecar, `<feedId>.flow`) | `<root>/{Exchange}/{Asset}/aggregated/{FeedId}/*.csv` |
| Side (top-level: `funding-rate`, `candle-ext`, …) | `<root>/{Exchange}/{Asset}/{FeedId}/*.csv` |

Single `*.csv` glob handles single-file and `.pNN` overflow uniformly.

### 9.4 `Int64Bar` & flow access

Alt bars share `Int64Bar` (6 longs, 48 B); strategies/indicators/exporters/charts consume it without knowing how the bar was produced. Invariants: `TimestampMs` strictly monotonic; `Low ≤ Open, Close ≤ High`; `Volume ≥ 0`.

Flow data (imbalance, buy/sell, overshoot) lives in the sidecar and is read via a typed `IFeedContext` accessor (no magic strings):

```csharp
public interface IFeedContext
{
    // Phase 4 — span return makes "do not hold across bars" a compile-time invariant.
    bool TryGetLatest(string feedKey, out ReadOnlySpan<double> values);
    bool HasNewData(string feedKey);
    DataFeedSchema GetSchema(string feedKey);

    // New (Phase 2b, default-interface methods).
    bool TryGetPrimarySidecar(out ReadOnlySpan<double> values) { values = default; return false; }
    PrimarySidecarSchema? PrimarySidecarSchema => null;

    double GetPrimarySignedImbalance() =>
        PrimarySidecarSchema is { } s
        && s.Columns.IndexOf("signed_imbalance") is var i and >= 0
        && TryGetPrimarySidecar(out var v)
            ? v[i] : double.NaN;
}

public sealed record PrimarySidecarSchema(IReadOnlyList<string> Columns);
```

`ReadOnlySpan<double>` is `ref struct` — it cannot be stored in a field, so "do not hold across bars" becomes a compile-time guarantee instead of a documented runtime hope. Trade-off: spans cannot cross async boundaries. `IFeedContext` consumers in backtest/live are synchronous bar handlers, so this works; future async streaming aggregation (out-of-scope per §1 non-goals) would need an async-friendly twin (likely `ReadOnlyMemory<double>` returning a per-bar snapshot copy, accepted as a one-time cost on the async path). Every existing `IFeedContext` impl changes signature in Phase 4 alongside the rest of the §9 redesign.

Engine binds `TryGetPrimarySidecar` to the FeedSeries named by the primary's `sidecar` field. Strategies that don't call it pay zero cost (lazy load). DIM fallback to a separate `ISidecarReceiver` if Phase 0 audit blocks DIMs on plugin assemblies.

### 9.5 Loader signature (Phase 1a, breaking)

```csharp
TimeSeries<Int64Bar> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to);

public readonly record struct DataFeedDescriptor(
    string DataRoot, string Exchange, string Asset, string FeedId, DataFeedKind Kind);
```

`PartitionedCsvBarLoader` gains `Kind`-based path resolution. Done in Phase 1a to avoid forced refactor of half the Application layer later. Legacy `CsvInt64BarLoader` (flat `{Year}/{YYYY-MM}.csv`) deleted in Phase 1a — no remaining call sites after migration.

### 9.6 Optimization, validation, debug

- **Backtest / Debug:** single Primary, list of side feeds.
- **Optimization:** `BacktestInputs.Primary` becomes `IReadOnlyList<DataFeedSubscription> PrimaryCandidates`; engine fans out across primaries × parameter grid.
- **Validation (walk-forward / OOS):** same Primary; range split server-side.

## 10. UI

### 10.1 Data Tab

New top-level tab left of "Backtest". Per-exchange expandable cards. Inside an exchange: data grid with assets as rows, feeds as columns. Columns dynamic, union of feeds across the exchange's displayed assets. Order: time bars (canonical), aggregated (grouped by type, threshold asc), ticks, side feeds (rightmost, dimmed). For exchanges with high feed cardinality (e.g., Binance with hundreds of symbols × dozens of feeds), the grid uses horizontal virtualization. Columns absent on every visible row are hidden. Display names use the §3.3 grammar throughout (lowercase `1m`, never `M1`).

Cells: `+` (clickable, opens right sidebar) or `−`. Sidecar-bearing aggregated cells render an indicator dot.

**Right sidebar (two cards):**
- **Status** — read-only Monaco viewer: feed's `feeds.json` entry (or partition-derived for time bars/ticks).
- **New aggregate bar** — Source/Type/N/Aggregate. Type filtered by source eligibility (§5.3); N input accepts SI (`1k`, `10M`); Aggregate disabled when `eligible_types: []`.

Aggregate click locks the command panel; status panel renders SSE progress (`Queued (#N)` → `Aggregating <YYYY>-<MM> … X%`). Rest of tab stays interactive. On success: column appears, `−` flips to `+`, toast with `actual_overshoot_pct`. On failure: status panel shows `ProblemDetails`; command panel re-enables.

**Warnings:** time-bar EqI shows yellow banner ("taker-buy proxy underestimates intra-bar churn; rebuild from `ticks` for magnitude-sensitive use"). Built feed's Status card carries the same banner.

### 10.2 Backtest / Optimization launch

- Primary feed dropdown from `/api/data/.../feeds`. Friendly labels (`EqV/1m:1k`, `1m`, `ticks`); icons distinguish time vs alt.
- Side-feed multi-select now accepts alt bars.
- Optimization: Primary becomes a multi-select chip control; UI shows estimated run count = `primaries × param_combos`.

## 11. Phased Rollout

**Phase 0 — Audits + locked decisions.**
- DIM audit on `IFeedContext`: `AssemblyLoadContext.Default` semantics on .NET 10, all impls in public+private repos, JIT DIM dispatch. Fall back to `ISidecarReceiver` if unsafe.
- `IInt64BarLoader` external-consumer audit (private repo, plugins).
- `TimeFrame` raw-`TimeSpan` overload audit: enumerate every callsite of `IInt64BarStrategy`/loader/subscription APIs that takes raw `TimeSpan`. Phase 4 removal scope is bounded by this enumeration.
- **Threshold input semantics locked** (resolves §12 item 1 before Phase 1a manifest schema ships): stored value in `feeds.json` is **always absolute** in canonical units (§3.4). The aggregation request accepts an `input_mode` ∈ `{ "absolute", "convenience" }`; convenience mode (e.g., "1k of 1m candles") is converted server-side at job creation and the original input is preserved in `threshold.convenience_input`. FE form sends `input_mode` explicitly; no ambiguous on-the-wire values.

**Phase 1a — Foundation (storage + loader signature).** Lands as one PR; bakes for one week before 1b.
- Storage convention §3 (layout, naming grammar, type convention §3.6).
- `IInt64BarLoader` signature → `DataFeedDescriptor` (§9.5). `PartitionedCsvBarLoader` glob-based listing **with per-FeedId filter** (regression tests for mixed-timeframe `candles/`). Delete legacy `CsvInt64BarLoader`.
- Manifest atomicity service (§4.1): startup sweep, staging-dir overwrite, per-`(exchange, asset)` synchronized writer with the read-merge-write protocol.
- `feeds.json` extension with fidelity block (manifest schema only — no aggregator yet).
- Apply pinned scale alignment (§3.4): `ScaleContext.QuantityScale`, scale-tag assertion at accumulator-entry call sites (the assertion is wired even though the only "accumulator" entry in 1a is a no-op — it locks the contract).
- `CsvFeedSeriesLoader` NaN parsing gated on `nullable_columns: true`.

**Phase 1b — Aggregation (REST + queue + accumulators).**
- `EqT`/`EqV`/`EqD` accumulators (§6.3) — time-bar source only. Side-feed `double → long` conversion at sum site per §3.6.
- HistoryLoader.WebApi endpoints: `POST /aggregate` (202, async-only), `GET /aggregations/{jobId}/progress` (SSE), `GET /aggregations/{jobId}` (snapshot), `DELETE /feeds/{id}`. Per-outcome-`feed_id` 423 dedup.
- In-memory job queue + worker pool (§6.5): `IAggregationJobQueue`, `AggregationWorkerHost`, `IAggregationJobRegistry`.
- **`AggregatorBenchmarks`**: BDN harness mirroring `BacktestBenchmarks`; required scenarios `EqV_1m_1000` and `EqT_1m_500` over BTCUSDT 5y (Mean + Allocated). Phase 1b merge gate via `scripts/perf/save-baseline.ps1` + `compare-baseline.ps1`.

**Phase 2a — Tick infrastructure.**
- Tick ingestion writing §3.5 daily partitions with `agg_id` resume.
- Tick reader; `feeds.json` `ticks` registration.
- Aggregator tick-source mode (chronological across daily partitions).
- Validation: re-run `EqV`/`EqD`/`EqT` from ticks on BTCUSDT_perp × 1y; confirm `actual_overshoot_pct ≤ 0.05%` vs. time-bar baseline.
- **No EqI yet** (intentional split; tick storage + signed accumulator are independent surfaces).

**Phase 2b — EqI + sidecar + `IFeedContext` extension.**
- Signed accumulator, EqI with `tick_signed`/`m1_taker_buy_proxy` tagging.
- `.flow` sidecar writer + reader (post-Q3 `CsvFeedSeriesLoader`).
- `IFeedContext.TryGetPrimarySidecar` + `PrimarySidecarSchema` (DIM, fallback per Phase 0).
- UI fidelity warning for time-bar EqI.

**Phase 3 — Main API proxy + Data Tab UI.**
- `/api/data/*` mirrors, typed `HistoryLoaderClient`, event-driven cache invalidation, SSE pass-through.
- Data Tab UI with sidecar grouping.

**Phase 4 — Subscription redesign + run-launch UI.**
- `DataFeedSubscription` end-to-end through Application + WebApi.
- Optimization fan-out across `PrimaryCandidates`.

**Phase 5 — Range / Renko accumulators.** Path-dependent; require ticks or sub-minute time bars.

**Phase 6 (if needed) — Durable job queue.** Persist queue (e.g., SQLite); SSE replay from event log; survive restart.

## 11A. Test Coverage

Per-phase test scope. Each bullet is a behavior, not a method — the named test fixture/class is a hint, not a contract. All run under the **single `dotnet` process at a time** rule (CLAUDE.md). xUnit + NSubstitute throughout.

### Phase 1a — storage + loader signature

- **Mid-month overflow rename (§3.2).** Write `2026-04.csv` to ~99 MB → next bar pushes it past 100 MB → assert: (a) original file atomic-renamed to `2026-04.p01.csv`, (b) new bar lands in freshly opened `2026-04.p02.csv`, (c) lex sort matches chronological order, (d) subsequent months pre-open as `.p01.csv`. Fixture: `PartitionOverflowTests`.
- **Sticky overflow.** Once a month rolls to part-numbered, the rest of that month MUST stay part-numbered even if size drops. Assert no `2026-04.csv` reappears after rollover.
- **Glob-based reader (§9.5).** `PartitionedCsvBarLoader` over `aggregated/<feedId>/*.csv` enumerates a mix of `2026-04.csv`, `2026-05.p01.csv`, `2026-05.p02.csv` in chronological order. Mixed-timeframe `candles/` regression: a per-FeedId filter excludes `2026-04_5m.csv` when loading `1m`.
- **Startup sweep — orphan tmp (§4.1).** Plant `*.tmp` files under `aggregated/EqV_1m_1000/` and `EqV_1m_1000.flow/`; boot HistoryLoader; assert all `*.tmp` deleted, real `.csv` partitions untouched.
- **Startup sweep — orphan feed dir.** Create `aggregated/Orphan_1m_999/2026-04.csv` with **no** `feeds.json` entry; boot; assert directory recursively deleted **and** WARN log line emitted with the absolute path (capture via Serilog test sink).
- **Startup sweep — manifest entry without dir.** Manifest references `EqV_1m_2000` but no `aggregated/EqV_1m_2000/` exists; sweep MUST NOT delete the manifest entry (only the inverse direction is destructive). Lock this with a test so a future "symmetric cleanup" refactor doesn't lose state.
- **Concurrent manifest writers (§4.1 read-merge-write).** Two tasks aggregate distinct `feed_id`s on the same asset, both finalize concurrently. Assert: both entries present in final `feeds.json`; no entry overwritten; `*.tmp` cleaned up. Repeat 100× to flush out lock-ordering races. Use `ManualResetEventSlim` to align finalizers within ~1 ms on every iteration (not just the first). Tag with `[Trait("Category", "Stress")]` so it can be excluded from the default `dotnet test` filter if it ever flakes on shared CI.
- **Cross-volume rename guard.** When `.staging-<jobId>/` somehow ends up on a different volume (test plants it deliberately), the writer MUST surface a clear error rather than silently performing a non-atomic copy. Skipped on CI if only one volume available.
- **Scale-tag assertion (§3.4).** Mismatched `ScaleContext` between source feed and accumulator entry throws at write-time. Verify the assertion is wired even though Phase 1a's "accumulator" is a no-op.
- **`CsvFeedSeriesLoader` NaN parsing.** `nullable_columns: true` in manifest → empty cells parse to `NaN`. `nullable_columns: false` (or unset) → empty cell throws. Pin the gating behavior.
- **Legacy `CsvInt64BarLoader` removed.** Solution-wide grep test (a `dotnet test`-runnable assertion over `Type.GetType("CsvInt64BarLoader, ...")` returning null) prevents accidental re-introduction.
- **Manifest required-field validation.** A non-EqI feed entry that omits `fidelity.imbalance_reconstruction_method` (the §4 "MUST be present even on non-EqI feeds" rule) round-trips as a manifest validation error at write time AND read time. Pin the rule on both sides — the writer is correct today, but a future refactor that loosens deserialization would silently re-allow malformed manifests.

### Phase 1b — aggregation pipeline + REST + queue

- **EqT/EqV/EqD accumulators (§6.3).** Synthetic 1m source where bar-emission boundary is hand-computable; assert emitted bar OHLC = first-open/max-high/min-low/last-close; bar `vol` = sum of source `vol`; `realized_threshold ≥ N` always.
- **Overshoot stats (§6.4).** Drive the accumulator with a source whose median record is `0.5N` (overshoot dominated by source granularity); assert `actual_overshoot_pct` ≈ analytic `100 / (2 × n_factor)` within tolerance. Max-overshoot tracked across the run.
- **Side-feed double→long conversion (§3.6 sum site).** `candle-ext.taker_buy_vol` arrives as `double`; assert `MoneyConvert.ToLong(value * QuantityScale)` is the only conversion path (mock the converter, verify call count = 1 per source bar).
- **Streaming memory bound (§6.2).** Aggregate 1y of 1m source; assert peak managed-heap allocations are O(write-buffer), not O(input). BDN `[MemoryDiagnoser]` cross-check via `AggregatorBenchmarks`.
- **POST 202 + SSE happy path (§5.4).** `POST /aggregate` → 202 with `Location` and `X-Job-Id` → SSE stream emits `queued? → started → progress* → complete` in order, `complete` payload matches snapshot `summary` shape exactly.
- **Per-outcome `feed_id` 423 dedup — active vs terminal (§6.5 fix).** Enqueue twice while job in `running` → second returns 423 with `existing_job_id`. Wait for `complete`; enqueue again within retention window → 202 (NOT 423). Wait for retention to expire → 202 (clean). Three distinct paths, three tests.
- **Reconnect / `Last-Event-ID` resume (§5.4).** Disconnect mid-stream; reconnect with `Last-Event-ID`; assert next event is the one immediately following. Without `Last-Event-ID`, server emits a synthetic `started + progress` snapshot before live events resume.
- **Reconnect after retention.** Reconnect 16 min after `complete` → 410 Gone with stable error code; FE-flow test confirms catalog-refetch fallback path activates.
- **Atomic finalize.** Kill the worker after partition writes but before manifest write; restart; sweep cleans staging; catalog reflects "feed missing"; no half-state visible to readers. Repeat with kill **after** manifest write — assert feed visible and complete.
- **Worker pool concurrency (§6.5).** Submit 4 distinct-feedId jobs with `maxConcurrentJobs=2`; assert exactly 2 run at a time; queue depth observable via `GET /aggregations/{jobId}` `queue_position`.
- **Threshold input modes (Phase 0 resolution).** `input_mode=absolute` with `threshold=1000` → manifest stores `1000`, `convenience_input=null`. `input_mode=convenience` with `convenience_input="1k 1m"` → server resolves to absolute, stores both. Round-trip test against the final `feeds.json` payload.

### Phase 2a — tick infrastructure

- **Tick monotonicity bump (§6.3).** Feed 50 ticks at identical exchange ms; assert emitted bars have `ts_open` strictly increasing by `+1 ms` cluster-internal; assert `Int64Bar.TimestampMs` invariant holds across the partition; assert aggregator stats record N bumps for the cluster.
- **Tick-source EqT/EqV/EqD parity.** Re-run the Phase 1b accumulator scenarios against a tick source that aggregates back to the same 1m baseline; assert `actual_overshoot_pct ≤ 0.05%` (the §11 Phase 2a validation criterion encoded as an automated test, not a manual one).
- **`agg_id` resume.** Crash mid-day-partition; resume reader; assert no duplicate ticks consumed and no ticks skipped at the boundary.

### Phase 2b — EqI + sidecar + IFeedContext

- **EqI tick-signed.** Drive ticks with a known buy/sell mix; assert `signed_acc` accumulates `+qty` on `is_buyer_maker=0` and `-qty` on `is_buyer_maker=1`; bar emits at `abs(signed_acc) ≥ N`.
- **EqI taker-buy proxy.** Same scenario via `candle-ext.taker_buy_vol`; assert `signed_imbalance = 2 * taker_buy - vol`; assert manifest `imbalance_reconstruction_method = "m1_taker_buy_proxy"`.
- **Sidecar zero-cost (§9.4 lazy load).** Strategy that does **not** call `TryGetPrimarySidecar` triggers no sidecar `CsvFeedSeriesLoader.Load` (verify via mocked loader, `Received(0)`).
- **Sidecar binding correctness.** `feeds.json` `sidecar` field auto-binds; mismatched/missing sidecar yields a clear error at engine init, not silent NaN-soup at runtime.
- ~~**`ReadOnlySpan<double>` field-storage compile-time check.**~~ Removed: `ref struct` field-storage prohibition is a C# language rule enforced by the compiler unconditionally. A negative-compile test would only re-verify the language spec, not anything project-specific.

### Phase 3 — main API proxy + Data Tab UI

- **SSE pass-through.** Main API forwards SSE chunks without coalescing; assert chunked transfer-encoding preserved and `IHttpResponseBodyFeature.DisableBuffering()` is invoked on the proxy route.
- **Cache invalidation.** `POST aggregate` (success) clears affected `(exchange, asset)` and catalog keys; concurrent reader does NOT see stale catalog.
- **Catalog payload shape (§5.1).** `/api/v1/assets` round-trips through main API unchanged; FE contract test pins the JSON.

### Phase 4 — subscription redesign

- **Polymorphic deserialization (§9.2).** `DataFeedSubscription` JSON discriminator covers all four subtypes; round-trip test.
- **`AltBarSubscription.FeedId`.** Format `{Type.Code}_{SourceFeedId}_{Threshold}` matches §3.3 grammar exactly; collision-detection test asserts no two subscriptions with the same components produce different `FeedId`.
- **Engine glob resolution (§9.3).** Each `Kind` resolves to the correct glob; sidecar (`Side` with `<feedId>.flow`) routes to the nested `aggregated/<feedId>/` glob, top-level side feeds (`funding-rate`) route to the asset-root glob.
- **Optimization fan-out.** `PrimaryCandidates × params` produces `|primaries| × |combos|` runs; deduplication via `IParameterNormalizer` still applies per-primary.

### Cross-cutting

- **No `decimal` in storage layer.** Solution-wide test that walks every type implementing the read/write CSV interfaces and asserts no `decimal` field/property. Pins §3.4.
- **No raw `(long)` casts.** The existing project-wide rule (CLAUDE.md "Int64 Money Convention") flags `(long)` outside `MoneyConvert` and `tests/`. Phase 1b confirms the aggregator's `HistoryLoader.Application/Aggregation/**` paths are within scope of the existing assertion rather than adding a parallel one.

## 12. Open Items

1. **Threshold convenience input UX.** ~~Open.~~ **Resolved in Phase 0** (see §11): store absolute in `feeds.json`; aggregation request carries explicit `input_mode` ∈ `{absolute, convenience}`; convenience input is preserved in `threshold.convenience_input` for traceability. Sketch wording ("1k 1m") is for FE display only — wire format is unambiguous.
2. **Multi-instance HistoryLoader.** v1 single-instance via `IOptions<HistoryLoaderOptions>{ BaseUrl }`. YARP is the upgrade path.
3. **Resumable / partial aggregations.** v1 always full-range; `from_ts`/`to_ts` are not in the v1 request schema. Phase 6 reintroduces them with real semantics (partitions are calendar-aligned and atomic per partition, so incremental rebuild is a natural extension).
4. **Plugin ABI for `IFeedContextReceiver`.** Confirm interface is public/stable so private-repo strategies compile unchanged. Validated by Phase 0 DIM audit.
5. **Re-aggregation from alt-bar sources.** `EqV_2000` from existing `EqV_1000` (≈10× fewer source records than re-running over time bars). Deferred to Phase 6: requires an alt-bar source reader that respects variable bar duration and a fidelity-equivalence proof (re-aggregation must produce the same bar boundaries as a fresh aggregation modulo accumulator initialization).
