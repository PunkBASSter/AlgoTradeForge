# TRD: Alternative Bar Aggregations in AlgoTradeForge

**Status:** Draft v1
**Scope:** HistoryLoader + AlgoTradeForge main API + Web UI
**Author:** _to be filled_

---

## 1. Overview

Add first-class support for **information-driven bars** (tick, volume, dollar, imbalance) and a future-proof slot for **path-dependent bars** (range, Renko) in AlgoTradeForge. Bars are produced by aggregating data already stored by HistoryLoader (`1m`/`5m`/`1h`/`1d` OHLCV, or raw ticks). Aggregated feeds become first-class citizens: they are persisted alongside time bars in HistoryLoader storage, exposed through HistoryLoader's REST API, listed and managed via a new "Data" tab in the AlgoTradeForge UI, and selectable as **primary** OHLC input for backtest, optimization, validation, and debug runs (in addition to remaining usable as side feeds).

The design takes a clean break with the current `DataSubscription` (no backward compatibility) so that the data-feed model scales to any future bar type with no further refactors.

## 2. Goals

- Persisted alternative-bar feeds reusable across runs without re-aggregation.
- Sync, streaming, memory-bounded aggregation on HistoryLoader (works for years of `1m` data in seconds; minutes for tick sources).
- Storage convention and DataSubscription model that scale to any future bar type with no further refactors.
- UI for inspecting available feeds per exchange/asset and triggering new aggregations.
- Per-feed metadata including overshoot error so the user can judge fidelity at a glance.

## 3. Non-Goals

- Real-time / streaming aggregation of live ticks (this TRD covers historical only).
- Building Range/Renko bars in v1 — slots are reserved in the convention but implementation is deferred.
- Cross-exchange composite feeds.
- Migration of any existing artifacts. No backwards compatibility required.

## 4. Glossary

| Term | Meaning |
|------|---------|
| Source feed | The persisted data used as input: `1m`, `5m`, `1h`, `ticks`, etc. |
| Aggregated feed | The persisted output: an alternative bar series. |
| Threshold (N) | The aggregation parameter (volume per bar, ticks per bar, signed-imbalance threshold, ...). Replaces "timeframe" for non-time bars. |
| Overshoot | Per-bar excess over the nominal threshold caused by the granularity of the source feed. Source of fidelity error when aggregating from a time bar vs. ticks. |
| Bar type code | Compact identifier (`EqT`, `EqV`, `EqD`, `EqI`, `Range`, `Renko`). |
| Feed ID | Stable identifier for a feed within `(exchange, asset)`; used in API, DataSubscription, and as the suffix of partition filenames in `candles/` or `aggregated/`. |

## 5. Storage Convention

### 5.1 Existing layout (confirmed)

Per-asset data is stored in **CSV files** partitioned monthly, with timeframe encoded in the filename suffix, all under a single `candles/` subdirectory:

```
History/
└── binance/
    └── BTCUSDT_perp/
        └── candles/
            ├── 2019-09_1m.csv
            ├── 2019-09_1h.csv
            ├── 2019-09_1d.csv
            ├── 2019-10_1m.csv
            ├── 2019-10_1h.csv
            ├── 2019-10_1d.csv
            └── ...
```

Filename grammar: `<YYYY>-<MM>_<TimeframeCode>.csv`. Timeframe codes are lowercase short forms: `1m`, `1h`, `1d` (and presumably `5m`, `15m`, `4h` if/when added).

> **Assumption (§15-4):** tick data, if/when stored, lives in a sibling `ticks/` directory with daily or monthly partitions (`<YYYY>-<MM>-<DD>.csv` or `<YYYY>-<MM>.csv`). Confirm format when tick storage is implemented; alt-bar source-code naming (§5.3) reserves `ticks` for it.

### 5.2 Aggregated feeds layout (new)

Aggregated bars get a sibling directory `aggregated/`. The same monthly-partition + composite-suffix convention is reused — the suffix just becomes richer:

```
History/
└── binance/
    └── BTCUSDT_perp/
        ├── candles/
        │   ├── 2019-09_1m.csv
        │   ├── 2019-09_1h.csv
        │   └── ...
        ├── ticks/                                  # if/when present
        │   └── ...
        └── aggregated/
            ├── 2019-09_EqV_1m_1000.csv             # Equal-Volume from 1m, N=1000
            ├── 2019-10_EqV_1m_1000.csv
            ├── 2019-09_EqD_1m_1000000.csv          # Equal-Dollar from 1m, N=$1M
            ├── 2019-09_EqT_ticks_5000.csv          # Equal-Tick from ticks, 5k
            ├── 2019-09_EqI_ticks_500000.csv        # Equal-Imbalance from ticks
            ├── EqV_1m_1000.status.json             # one status file per feed (not per partition)
            ├── EqD_1m_1000000.status.json
            ├── EqT_ticks_5000.status.json
            └── EqI_ticks_500000.status.json
```

### 5.3 Naming grammar

**Aggregated partition file:**
```
<YYYY>-<MM>_<TypeCode>_<SourceCode>_<Threshold>.csv
```

**Status file (one per feed, lives at `aggregated/` root):**
```
<TypeCode>_<SourceCode>_<Threshold>.status.json
```

**Feed ID** (used in API responses, DataSubscription, UI keys) is the suffix without month or extension:
```
<TypeCode>_<SourceCode>_<Threshold>
```
Example: `EqV_1m_1000`.

| Field | Values |
|-------|--------|
| `TypeCode` | `EqT`, `EqV`, `EqD`, `EqI`, (future) `Range`, `Renko` |
| `SourceCode` | `1m`, `5m`, `15m`, `1h`, `4h`, `1d`, `ticks` (lowercase, matches existing convention) |
| `Threshold` | positive integer; units defined per type, see §5.4 |

Display name uses colon and SI suffixes: `EqV_1m_1000` ↔ `EqV/1m:1k`, `EqD_1m_1000000` ↔ `EqD/1m:1M`. The UI auto-formats absolute integers into SI for column headers. The sketch's compact form `EqV1m:1k` is supported where horizontal space is tight.

**Partition assignment:** an aggregated bar lands in the partition file matching the `YYYY-MM` of its `ts_open`. Bars that span a month boundary still go in the open-month file — there is no splitting. Edge case for very large thresholds (multi-month bars): partitions can be empty in months where no bar opened. That's fine; the feed reader handles gaps.

### 5.4 Threshold units per type

| TypeCode | Threshold meaning | Unit |
|----------|-------------------|------|
| EqT | Trade/tick count per bar | trades |
| EqV | Cumulative base-asset volume per bar | base asset units |
| EqD | Cumulative quote-asset (dollar) volume per bar | quote currency units |
| EqI | Absolute cumulative signed dollar volume per bar | quote currency units (signed accumulator with abs threshold) |
| Range | Price travel per bar (future) | price points / ticks |
| Renko | Brick size (future) | price points / ticks |

Threshold is **always stored as an absolute integer** in the filename. Convenience input modes (e.g. "≈1000 1-minute candles' worth of volume") are resolved to absolute values at the UI/API boundary; the resolved value is the canonical one and goes into both filename and `status.json`. The convenience input is preserved separately in `status.json` for traceability.

### 5.5 CSV schemas

**Existing `candles/<YYYY>-<MM>_<tf>.csv` schema (assumed Binance kline columns; §15-1 to confirm):**

```
open_time, open, high, low, close, volume,
close_time, quote_volume, num_trades,
taker_buy_base_volume, taker_buy_quote_volume
```

This schema is left untouched.

**New `aggregated/<YYYY>-<MM>_<TypeCode>_<SourceCode>_<Threshold>.csv` schema:**

```
bar_index, ts_open, ts_close,
open, high, low, close,
volume, quote_volume, num_trades,
buy_volume, sell_volume,
realized_threshold, signed_imbalance
```

Column semantics:

- `bar_index` — monotonically increasing within the feed across all partitions (continues across month boundaries).
- `ts_open`, `ts_close` — wall-clock bar open/close. Variable spacing (a fundamental property of alt bars).
- `open`/`high`/`low`/`close` — OHLC of the bar.
  - From `1m`/`1h`/etc. source: `open` = first source candle's open; `high`/`low` = max/min over source candles; `close` = last source candle's close.
  - From `ticks` source: derived per-tick.
- `volume`, `quote_volume`, `num_trades` — sums over the bar's input.
- `buy_volume`, `sell_volume` — split, in base-asset units.
  - From `1m`-class source: `buy_volume = sum(taker_buy_base_volume)`, `sell_volume = volume − buy_volume`.
  - From `ticks` source: per-tick from `isBuyerMaker`.
  - Empty (blank cell) if source feed lacks the data.
- `realized_threshold` — actual accumulator value at bar close (≥ nominal threshold; the difference is the overshoot for that bar).
- `signed_imbalance` — only populated for EqI; signed accumulator value at close. Empty for other types.

Header row is included in every partition file. Empty `buy_volume`/`sell_volume`/`signed_imbalance` cells are written as empty string (not `0`, to distinguish "unavailable" from "exactly zero").

## 6. Status JSON

Each aggregated feed has a `status.json` co-located at `aggregated/<feedId>.status.json`. Time-bar and Tick feeds do not maintain status files on disk; the API generates a minimal status response on demand (§7.2) by scanning their CSV partitions for first/last timestamp and total record count.

```json
{
  "schema_version": 1,
  "feed_id": "binance/BTCUSDT_perp/EqV_1m_1000",
  "type": {
    "code": "EqV",
    "name": "EqualVolume"
  },
  "source": {
    "exchange": "binance",
    "asset": "BTCUSDT_perp",
    "feed": "1m",
    "first_ts": "2019-09-01T00:00:00Z",
    "last_ts":  "2026-04-28T00:00:00Z",
    "record_count": 3458880
  },
  "threshold": {
    "value": 1000,
    "unit": "base_asset",
    "input_mode": "absolute",
    "convenience_input": null
  },
  "build": {
    "tool_version": "1.4.0",
    "built_at": "2026-04-28T11:23:51Z",
    "duration_seconds": 38,
    "bar_count": 18421,
    "partitions_written": ["2019-09", "2019-10", "...", "2026-04"]
  },
  "fidelity": {
    "estimated_overshoot_pct": 2.5,
    "actual_overshoot_pct": 2.42,
    "max_overshoot_pct": 18.7,
    "median_source_record_value": 0.5,
    "n_factor": 2000,
    "imbalance_reconstruction_method": null
  },
  "first_bar_ts": "2019-09-01T00:04:12Z",
  "last_bar_ts":  "2026-04-27T23:51:06Z"
}
```

Fidelity field semantics:

- `estimated_overshoot_pct` — analytic prediction `100 / (2 * n_factor)` where `n_factor = threshold / median_source_record_value`. Computed *before* aggregation runs and used to warn the user when N is small.
- `actual_overshoot_pct` — measured during aggregation: mean over all bars of `(realized_threshold − threshold) / threshold * 100`.
- `max_overshoot_pct` — worst-case overshoot in the dataset (catches whale-trade artifacts).
- `imbalance_reconstruction_method` — only set for `EqI`; one of `"tick_signed"` (true Lee-Ready / `isBuyerMaker`) or `"m1_taker_buy_proxy"` (approximation; underestimates intra-bar two-way pressure when source is a time bar). UI surfaces a warning for the proxy variant.

For aggregations from `ticks`, the analytic estimate approaches zero (per-tick granularity), and `actual_overshoot_pct` should be ≤ 0.05 % in practice.

## 7. HistoryLoader REST API

All endpoints are sync HTTP. Response models normalize naming conventions across exchanges.

### 7.1 Discovery

```
GET /api/v1/exchanges
```
→ list of exchange codes with available asset counts.

```
GET /api/v1/exchanges/{exchange}/assets
```
→
```json
[
  {
    "asset": "BTCUSDT_perp",
    "asset_class": "perp",
    "feeds": [
      { "id": "1m",          "kind": "OHLCV_TimeBar", "size": 3458880,    "first_ts": "...", "last_ts": "..." },
      { "id": "1h",          "kind": "OHLCV_TimeBar", "size": 57648,      "first_ts": "...", "last_ts": "..." },
      { "id": "1d",          "kind": "OHLCV_TimeBar", "size": 2402,       "first_ts": "...", "last_ts": "..." },
      { "id": "ticks",       "kind": "Tick",          "size": 412009993,  "first_ts": "...", "last_ts": "..." },
      { "id": "EqV_1m_1000", "kind": "OHLCV_AltBar",  "size": 18421,      "first_ts": "...", "last_ts": "..." }
    ]
  }
]
```

`feeds[].kind` drives UI grouping and aggregation eligibility:

- `OHLCV_TimeBar` — eligible source for some aggregations (per §11)
- `OHLCV_AltBar` — already aggregated; **not** a re-aggregation source in v1
- `Tick` — eligible source for ALL aggregation types
- `Side` — futures-only side feeds (funding, open interest, mark/index price); not eligible

### 7.2 Feed inspection

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status
```
→ the feed's status JSON.
- For aggregated feeds: returns the persisted `aggregated/<feedId>.status.json`.
- For time bars and ticks: generated on the fly by scanning CSV partitions (first/last partition month, total record count, data range), conforming to a minimal schema variant.

### 7.3 Aggregation eligibility

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options
```
→
```json
{
  "source_feed_id": "1m",
  "compatible_types": [
    {
      "code": "EqT", "name": "Equal Tick",
      "default_threshold": 5000, "threshold_unit": "trades",
      "estimated_fidelity_at_default_pct": 0.05
    },
    { "code": "EqV", "name": "Equal Volume",  "default_threshold": 1000,    "threshold_unit": "base_asset" },
    { "code": "EqD", "name": "Equal Dollar",  "default_threshold": 1000000, "threshold_unit": "quote_asset" },
    {
      "code": "EqI", "name": "Equal Imbalance",
      "default_threshold": 500000, "threshold_unit": "quote_asset",
      "fidelity_warning": "Imbalance from a time-bar source uses the taker-buy proxy and underestimates intra-bar two-way pressure."
    }
  ]
}
```

Time-bar-only and tick-only options are filtered server-side from the master compatibility matrix (§11).

### 7.4 Aggregation command

```
POST /api/v1/exchanges/{exchange}/assets/{asset}/aggregate
```

Body:
```json
{
  "source_feed_id": "1m",
  "type_code": "EqV",
  "threshold": 1000,
  "threshold_unit": "base_asset",
  "overwrite_existing": false,
  "from_ts": null,
  "to_ts":   null
}
```

Response (200 OK after aggregation completes):
```json
{
  "feed_id": "EqV_1m_1000",
  "status_path": "binance/BTCUSDT_perp/aggregated/EqV_1m_1000.status.json",
  "fidelity": { "actual_overshoot_pct": 2.42, "max_overshoot_pct": 18.7, "estimated_overshoot_pct": 2.5 },
  "duration_seconds": 38,
  "bar_count": 18421,
  "partitions_written": 80
}
```

**Sync timeout policy:** the API host configures Kestrel `KeepAliveTimeout` and any reverse proxy to **60 minutes** for `/aggregate`. The .NET client (`HttpClient`) uses a matching `Timeout`. Rationale and bounds in §9.1.

Errors:
- 409 Conflict — feed exists and `overwrite_existing=false`.
- 422 Unprocessable Entity — type incompatible with source.
- 423 Locked — another aggregation targeting the same `feed_id` is in progress (see §15-5).

### 7.5 Feed deletion

```
DELETE /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}
```

Allowed only for aggregated feeds (kind `OHLCV_AltBar`). Attempts on `OHLCV_TimeBar` / `Tick` / `Side` return 403.

## 8. AlgoTradeForge Main API (proxy layer)

All HistoryLoader endpoints in §7 are mirrored under `/api/data/...` on the main API with identical contracts. The main API:

- Forwards the request to the configured HistoryLoader instance.
- Adds AuthN / AuthZ (existing pipeline).
- Caches discovery responses (§7.1, §7.2) with a short TTL (5 s) to keep the Data tab snappy without showing stale states post-aggregation (the aggregation completion handler invalidates the cache for the affected asset).
- Translates HistoryLoader 5xx into structured `ProblemDetails` responses.

## 9. Aggregation Pipeline (HistoryLoader internals)

### 9.1 Sufficiency of sync HTTP

| Source | Symbols × Years | Approx. records | Aggregation wall time | Verdict |
|--------|-----------------|-----------------|------------------------|---------|
| `1m`   | 1 sym × 5 y     | ~2.6 M          | seconds                | sync OK |
| `1m`   | 50 sym × 5 y (sequential) | ~130 M | minutes                | sync OK |
| `ticks`| BTCUSDT × 1 y   | ~100 M          | minutes                | sync OK with 60-min HTTP timeout |
| `ticks`| BTCUSDT × 5 y   | ~500 M          | tens of minutes        | borderline; sync acceptable for v1, async-with-progress as v2 follow-up |

**Recommendation:** ship v1 with sync only. The UI's "lock + spinner" UX matches this. If tick-source jobs become a UX problem, add a Server-Sent Events progress endpoint without breaking the sync contract (the client subscribes to SSE in parallel; sync POST still returns the final summary).

### 9.2 Streaming aggregator design

```
PartitionedSourceReader                 (enumerates candles/<YYYY>-<MM>_<src>.csv
    │                                    or ticks/* in chronological order;
    │                                    streams rows across partition boundaries
    │                                    as a single logical stream)
    │  yields records in chronological order
    ▼
BarAccumulator (one per type)
    │  emits closed bar when threshold crossed
    ▼
PartitionedSinkWriter                   (routes each emitted bar to
    │                                    aggregated/<YYYY>-<MM>_<feedId>.csv
    │                                    by ts_open; rolls files at month boundary;
    │                                    writes header on each new partition)
    ▼
StatusFinalizer                         (writes aggregated/<feedId>.status.json)
```

Properties:

- **Memory bounded:** O(1) per accumulator, O(write_buffer_size) per partition. Independent of dataset size.
- **Pipelined:** stages connected via `System.Threading.Channels` so disk reads, accumulation, and writes overlap.
- **Batched I/O:** reader yields rows in chunks of 10 k; sink flushes every 5 k bars per partition.
- **Atomic per-partition writes:** each output partition is written to `<filename>.tmp` and atomically renamed on completion of the corresponding source-month boundary. The status file is written last; its presence is the "feed is complete" marker.
- **Single accumulator interface:**
  ```csharp
  public interface IBarAccumulator
  {
      bool TryAdvance(in SourceRecord r, out AggregatedBar emitted);
      AggregationStats Finalize();
  }
  ```

### 9.3 Per-type accumulators

| Type | Accumulator state | Emission rule |
|------|-------------------|---------------|
| EqT  | tick counter, OHLC, optional buy/sell split if source has it | counter ≥ N |
| EqV  | base-volume accumulator, OHLC | base_volume_acc ≥ N |
| EqD  | quote-volume accumulator, OHLC | quote_volume_acc ≥ N |
| EqI  | signed accumulator (per-tick `isBuyerMaker` from ticks; per-row taker-buy proxy from time bars), OHLC, abs comparator | abs(signed_acc) ≥ N |

OHLC of an aggregated bar:

- **From a time-bar source (`1m`, `1h`, …):** `O` = first source record's open; `H` = max of records' highs; `L` = min of records' lows; `C` = last source record's close; volumes summed; `buy_volume = sum(taker_buy_base_volume)`, `sell_volume = volume − buy_volume`.
- **From `ticks`:** `O` = first tick price; `H` = max tick price; `L` = min tick price; `C` = last tick price; volumes summed; buy/sell split derived per-tick from `isBuyerMaker`.

### 9.4 Overshoot computation

During aggregation, for each emitted bar:
```
overshoot_pct_i = (realized_threshold_i − threshold) / threshold * 100
```
Track running mean and max. Write both to `status.json` at finalization, alongside the analytic estimate computed before the run starts.

## 10. AlgoTradeForge API: DataSubscription Redesign

The current `DataSubscription` is replaced by a polymorphic hierarchy. No backward-compat shims; existing callers are migrated.

```csharp
public abstract record DataFeedSubscription(string Exchange, string Asset)
{
    public abstract string FeedId { get; }            // identifies the feed within (Exchange, Asset)
    public abstract DataFeedKind Kind { get; }
    public DataFeedRole Role { get; init; } = DataFeedRole.Side;
}

public enum DataFeedKind { TimeBar, AltBar, Tick, Side }
public enum DataFeedRole { Primary, Side }

public sealed record TimeBarSubscription(
    string Exchange, string Asset, TimeFrame TimeFrame)
    : DataFeedSubscription(Exchange, Asset)
{
    public override string FeedId => TimeFrame.Code;                 // "1m", "1h", "1d"
    public override DataFeedKind Kind => DataFeedKind.TimeBar;
}

public sealed record AltBarSubscription(
    string Exchange, string Asset,
    AltBarType Type, string SourceFeedId, long Threshold)
    : DataFeedSubscription(Exchange, Asset)
{
    public override string FeedId => $"{Type.Code}_{SourceFeedId}_{Threshold}";  // "EqV_1m_1000"
    public override DataFeedKind Kind => DataFeedKind.AltBar;
}

public sealed record TickSubscription(string Exchange, string Asset)
    : DataFeedSubscription(Exchange, Asset)
{
    public override string FeedId => "ticks";
    public override DataFeedKind Kind => DataFeedKind.Tick;
}

public sealed record SideFeedSubscription(string Exchange, string Asset, string SideFeedId)
    : DataFeedSubscription(Exchange, Asset)
{
    public override string FeedId => SideFeedId;                      // "funding", "oi", ...
    public override DataFeedKind Kind => DataFeedKind.Side;
}
```

### 10.1 Backtest input model

```csharp
public sealed class BacktestInputs
{
    public required DataFeedSubscription Primary { get; init; }   // Role must be Primary
    public IReadOnlyList<DataFeedSubscription> SideFeeds { get; init; } = [];
    public DateRange Range { get; init; }
}
```

Constraints (enforced at submission):

- Exactly one `Primary`.
- All side feeds may be any kind.
- `Primary` may be `TimeBarSubscription` or `AltBarSubscription` (alt bars are drop-in primaries).
- `TickSubscription` and `SideFeedSubscription` are only valid as side feeds.

### 10.2 Backtest engine I/O

The engine resolves a `DataFeedSubscription` to a glob over CSV partitions:

| Kind     | Glob |
|----------|------|
| TimeBar  | `<storage_root>/{Exchange}/{Asset}/candles/*_{FeedId}.csv` |
| AltBar   | `<storage_root>/{Exchange}/{Asset}/aggregated/*_{FeedId}.csv` |
| Tick     | `<storage_root>/{Exchange}/{Asset}/ticks/*.csv` |
| Side     | (TBD per side feed) |

Partitions are read in chronological order via a single CSV reader; the engine then consumes bars one by one regardless of whether they came from `candles/` or `aggregated/`. Schema differences between the two are handled at the reader layer (the time-bar reader maps existing kline columns to the engine's bar struct; the alt-bar reader uses the §5.5 schema and ignores the alt-bar-only columns the strategy doesn't need). **No engine-side awareness of how bars were produced** — that's the property that lets alt bars become primaries with zero engine changes.

Side feeds are exposed to strategies through the existing side-feed API; aggregated bars used as side inputs travel the same path.

### 10.3 Optimization, validation, debug

- **Backtest / Debug:** single primary, list of side feeds.
- **Optimization:** `BacktestInputs.Primary` is replaced by `IReadOnlyList<DataFeedSubscription> PrimaryCandidates` in `OptimizationInputs`; the engine fans out across primaries × parameter grid.
- **Validation (walk-forward / OOS):** uses the same primary feed as backtest; range is split server-side.

## 11. Compatibility Matrix (source feed → eligible bar types)

| Source kind | Eligible types | Notes |
|-------------|----------------|-------|
| Tick | EqT, EqV, EqD, EqI, (future) Range, Renko | All types; highest fidelity. |
| OHLCV_TimeBar with `number_of_trades`, `volume`, `quote_volume`, `taker_buy_*` (Binance klines all have these) | EqT, EqV, EqD, EqI* | EqI* has a fidelity warning (taker-buy proxy). |
| OHLCV_TimeBar with OHLC only, no V | (future) Range, Renko | Volume-based not buildable. |
| OHLCV_AltBar | none | Re-aggregation forbidden in v1. |
| Side (funding, OI, mark) | none | "Aggregate" button disabled in UI. |

The matrix is exposed via `/aggregation-options` (§7.3) so the UI renders the Type dropdown options dynamically without hard-coded lists.

## 12. UI: Data Tab

New top-level tab placed left of "Backtest". Layout matches the attached sketch.

### 12.1 Left panel — History tree

- Per-exchange sections, each an expandable card (`binance` open, `bybit` / `CMEX` collapsed by default).
- Inside an exchange: a data grid with assets as rows and feeds as columns.
- Columns are dynamic, built from the union of feeds across that exchange's assets. Stable column order:
  1. Time bars in canonical order (`1m`, `5m`, `15m`, `1h`, `4h`, `1d`, …).
  2. Aggregated bars grouped by type code, sorted by threshold ascending.
  3. `ticks` and other source-eligible feeds.
  4. Side feeds (least interesting) at the rightmost end (§12.4).
- Cell content: `+` if feed exists for that asset, `−` otherwise.
- `+` is clickable → opens the right sidebar with that feed's status (read-only Monaco editor showing the JSON from §6, persisted file or generated on the fly per §7.2).
- Asset row label is clickable → also opens sidebar, with the "New aggregate bar" form pre-populated for that asset (no source pre-selected).

### 12.2 Right sidebar — Status

Two stacked cards:

**Status card** — shows the active feed's `status.json`, read-only Monaco editor with JSON syntax highlighting and folding.

**New aggregate bar card** — form with:

- `Source` (auto-set when sidebar is opened from a `+` cell or asset row; editable dropdown of eligible source feeds for the asset, populated from `/aggregation-options`).
- `Type` (dropdown filtered by the Source's eligibility).
- `N` (integer input with format hint per type — e.g. "trades", "BTC", "USDT"). SI input accepted (`1k`, `10M`).
- `Aggregate` button — disabled until Source is `OHLCV_TimeBar` or `Tick`, and Type and N are valid.

Click `Aggregate` → call `POST /api/data/.../aggregate`.

- Whole tab UI locks behind a translucent overlay with a spinner and an elapsed-time counter.
- On success: a new column appears (or the existing column's `−` flips to `+`) and the sidebar refreshes to show the new feed's status. Toast confirms with `actual_overshoot_pct`.
- On error: toast + stay on form.

### 12.3 Aggregation eligibility surfacing

- The UI never offers types the source can't produce.
- The Aggregate button is inert (with tooltip) when:
  - Source is `OHLCV_AltBar` — "Re-aggregation not supported in v1."
  - Source is `Side` — "This feed cannot be used to build bars."

### 12.4 Optional column-ordering enhancement

Within columns, "aggregation-eligible" feeds (`Tick`, `OHLCV_TimeBar`) render with an icon prefix and are pulled left within their group. Pure consumers (alt bars) render plain. Side feeds render dimmed and right.

## 13. UI: Backtest / Optimization Launch

- **Primary feed selection:** replaces the current implicit `DataSubscription` form with an explicit "Primary feed" dropdown populated from `/api/data/exchanges/{exchange}/assets/{asset}/feeds`. Display label is the friendly form (`EqV/1m:1k`, `1m`, `ticks`) with iconography distinguishing time bars from alt bars.
- **Side feeds:** existing multi-select, now able to include alt bars too.
- **Optimization:** the Primary dropdown becomes a multi-select chip control; the engine fans out across selected primaries × parameter grid. UI shows estimated run count (`primaries × param_combos`).

## 14. Phased Rollout

1. **Phase 1 — Storage & HistoryLoader core.** Convention, accumulators (`EqT`, `EqV`, `EqD`), sync `/aggregate`, status.json with fidelity. Time-bar source only (`1m`/`1h`/`1d`).
2. **Phase 2 — Tick source + EqI.** Tick reader, signed accumulator, imbalance type. Fidelity warning for time-bar-source EqI.
3. **Phase 3 — Main API proxy + Data tab UI.**
4. **Phase 4 — DataSubscription redesign + backtest / optimization launch UI integration.**
5. **Phase 5 — Range / Renko accumulators.** Path-dependent; require ticks or sub-minute time bars.
6. **Phase 6 (if needed) — async-with-progress aggregation endpoint** for long tick jobs.

## 15. Open Questions / Assumptions

1. **Exact column set & header convention of existing `candles/*.csv`** — the schema in §5.5 is assumed to be Binance kline columns. Confirm exact column names, header presence, separator, and timestamp format (epoch ms vs ISO) used by the current writer. The alt-bar reader (and any time-bar reader the engine already has) need to share this. **No directory-layout question remains** — `History/{exchange}/{asset}/candles/<YYYY>-<MM>_<tf>.csv` is confirmed.
2. **EqI from a time-bar source** — within a single source candle we credit the *net* taker-buy-vs-sell as a single signed contribution. This systematically underestimates intra-bar imbalance (cancellations). Decide between (a) ship with `imbalance_reconstruction_method = "m1_taker_buy_proxy"` and a UI warning, or (b) restrict EqI to tick source. Recommendation: (a) for discoverability, (b) only if research shows the proxy is uselessly noisy.
3. **Threshold convenience input** — the sketch labels (`EqV1m:1k`, "Equal volume bar built of 1k 1m") could mean either "absolute 1000 base units" or "≈1000 1-minute candles' worth of volume". Recommendation: store absolute, accept either as input mode, surface both in `status.json` (`threshold.input_mode` + `threshold.convenience_input`). Confirm sketch's intent.
4. **Tick storage layout** — the `ticks/` directory is assumed sibling of `candles/`. Once tick ingestion is implemented, decide partition granularity (daily files for tick density, monthly for consistency with candles). Schema (`ts, price, qty, isBuyerMaker, agg_id`) needs to be locked before phase 2.
5. **Concurrent aggregations** — should the API serialize aggregations per `feed_id` to avoid two writes to the same target? Recommendation: yes, with a per-`feed_id` mutex; concurrent jobs targeting different feeds are allowed. Returns 423 Locked on collision.
6. **Partial / resumable aggregations** — v1 always aggregates the full source range. `from_ts` / `to_ts` are reserved in the API but treated as full-range until v2. Incremental rebuilds (only re-run the last partition when new source data arrives) are a natural extension since partitions are monthly and files are atomic per partition.
7. **Storage root path discovery** — assumed configurable on HistoryLoader; main API gets it via config. Multiple HistoryLoader instances per main API? v1 assumes a single instance.
8. **Empty cells in CSV** — the "empty string for unavailable, 0 for actual zero" convention (§5.5) needs the existing CSV reader to support nullable numeric columns. If it doesn't, fall back to a sentinel like `NaN` or omit the columns entirely from CSVs sourced from feeds that don't have the data.