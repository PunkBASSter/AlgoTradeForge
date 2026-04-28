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

Per-asset data is stored in **CSV files** partitioned monthly. Candles live under a `candles/` subdirectory; auxiliary side feeds (funding rate, open interest, etc.) live under sibling per-feed subdirectories (`funding_rate/`, `open_interest/`, …). The candle filename uses underscore-separated `YYYY-MM_{interval}` (matches `PartitionedCsvBarLoader.GetPartitionPath` at `Infrastructure/History/PartitionedCsvBarLoader.cs:107`):

```
History/
└── binance/
    └── BTCUSDT_perp/
        ├── candles/
        │   ├── 2019-09_1m.csv
        │   ├── 2019-09_1h.csv
        │   ├── 2019-09_1d.csv
        │   ├── 2019-10_1m.csv
        │   ├── 2019-10_1h.csv
        │   ├── 2019-10_1d.csv
        │   └── ...
        ├── funding_rate/
        │   └── 2019-09_8h.csv
        ├── open_interest/
        │   └── 2019-09_5m.csv
        └── feeds.json
```

Filename grammar (candles): `<YYYY>-<MM>_<TimeframeCode>.csv` (underscore between month and code). Timeframe codes are lowercase short forms: `1m`, `1h`, `1d` (and `5m`, `15m`, `4h` if/when added). Confirmed against `PartitionedCsvBarLoader.GetPartitionPath` (which uses `$"{month:yyyy-MM}_{intervalStr}.csv"`) and `FeedContextBuilder.Build`.

Filename grammar (side feeds): `<YYYY>-<MM>[_<Interval>].csv` under a `<feedName>/` subdirectory. Side-feed schemas are declared in `feeds.json` (per-asset) — column names + optional `autoApply` config. This existing scheme is reused for aggregated feeds (§5.2), not replaced.

> **Assumption (§15-4):** tick data, if/when stored, lives in a sibling `ticks/` directory with daily or monthly partitions (`<YYYY>-<MM>-<DD>.csv` or `<YYYY>-<MM>.csv`). Confirm format when tick storage is implemented; alt-bar source-code naming (§5.3) reserves `ticks` for it.

### 5.2 Aggregated feeds layout (new)

Aggregated bars get a sibling directory `aggregated/`. **Each aggregated feed gets its own subdirectory** named by `feedId`, with monthly partitions inside — mirroring the existing per-feed-directory layout used by `funding_rate/`, `open_interest/`, and the `.flow/` sidecars. One consistent partitioning model across the asset.

```
History/
└── binance/
    └── BTCUSDT_perp/
        ├── candles/
        │   ├── 2019-09_1m.csv
        │   ├── 2019-09_1h.csv
        │   └── ...
        ├── ticks/                                  # if/when present
        │   └── 2024-01-01.csv
        ├── funding_rate/                           # existing side feed
        │   └── 2019-09_8h.csv
        ├── aggregated/
        │   ├── EqV_1m_1000/                        # Equal-Volume from 1m, N=1000
        │   │   ├── 2019-09.csv
        │   │   ├── 2019-10.csv
        │   │   └── ...
        │   ├── EqD_1m_1000000/                     # Equal-Dollar from 1m, N=$1M
        │   │   └── 2019-09.csv
        │   ├── EqT_ticks_5000/                     # Equal-Tick from ticks, 5k
        │   │   └── 2019-09.csv
        │   └── EqI_ticks_500000/                   # Equal-Imbalance from ticks
        │       └── 2019-09.csv
        ├── EqI_ticks_500000.flow/                  # sidecar FeedSeries for EqI imbalance
        │   ├── 2019-09.csv
        │   └── ...
        └── feeds.json                              # registers candles + side feeds + aggregated feeds + sidecars
```

**Partition assignment:** a bar lands in the partition file matching the `YYYY-MM` of its `ts_open` (UTC). Multi-month bars (large `N`, low-activity assets) still land in the open-month file — there is no splitting; intervening months may have empty/missing files, which the chronological reader handles by skipping ahead.

**Size cap with weekly overflow.** The writer enforces a soft per-partition byte budget (default **100 MB**, configurable as `aggregator.maxPartitionSizeMB`). When a monthly partition would exceed the budget, the writer **splits that month into ISO-week files** named `<YYYY>-<MM>-W<n>.csv` (W1–W5). The default case (almost every realistic feed config) produces single monthly files; the overflow only fires for very-small-N tick-source feeds that would otherwise produce hundreds of MB per month.

```
aggregated/<feedId>/<YYYY>-<MM>.csv             # default — single file per month
aggregated/<feedId>/<YYYY>-<MM>-W<n>.csv        # overflow — week n (ISO week-of-month, 1–5) within the month
```

Reader behavior: `aggregated/<feedId>/*.csv` globs both forms; chronological order is maintained because the lexicographic sort of `YYYY-MM` and `YYYY-MM-W<n>` agrees with calendar order. The split decision per month is recorded in the feed's `feeds.json` entry (`build.partitions_written` is `["2019-09", "2019-10-W1", "2019-10-W2", ...]`) so re-readers don't have to introspect the filesystem to learn which months are split.

**Why monthly default + weekly overflow rather than always-weekly or always-daily:** realistic alt-bar configurations (1k–10k bars/day) produce <50 MB monthly partitions — well under the cap. Always-weekly forces 4–5 files/month for no benefit on the 95% case. Always-daily over-fragments (2–4 MB files) and breaks alignment with the side-feed convention. Monthly-with-overflow picks the right granularity *automatically per feed*. See §15-Q12 for the resolved discussion.

The sidecar feed is a regular `FeedSeries` declared in `feeds.json` exactly like `funding_rate` or `open_interest`, and follows the same monthly-with-weekly-overflow rule. The engine loads it through the existing `IFeedContextBuilder` / `CsvFeedSeriesLoader` path; strategies opt in via `IFeedContextReceiver` (§10.4). **No new infrastructure** is introduced for sidecars — they reuse what already ships.

### 5.3 Naming grammar

**Aggregated partition path:**
```
<TypeCode>_<SourceCode>_<Threshold>/<YYYY>-<MM>.csv          # default
<TypeCode>_<SourceCode>_<Threshold>/<YYYY>-<MM>-W<n>.csv     # weekly overflow (see §5.2)
```

**Sidecar `FeedSeries` directory** (created only for feeds that publish flow data — e.g. EqI imbalance, optional EqV/EqT/EqD `buy_volume`/`sell_volume`):
```
<TypeCode>_<SourceCode>_<Threshold>.flow/<YYYY>-<MM>.csv          # default
<TypeCode>_<SourceCode>_<Threshold>.flow/<YYYY>-<MM>-W<n>.csv     # weekly overflow
```

**Feed metadata** lives in the per-asset `feeds.json` (single source of truth — no separate per-feed `status.json` files). The `feeds` map gains entries for each aggregated feed and each sidecar; their schema is described in §6.

**Feed ID** (used in API responses, DataSubscription, UI keys) is the directory name:
```
<TypeCode>_<SourceCode>_<Threshold>
```
Example: `EqV_1m_1000`. The sidecar feed ID for an EqI primary `EqI_ticks_500000` is `EqI_ticks_500000.flow`.

| Field | Values |
|-------|--------|
| `TypeCode` | `EqT`, `EqV`, `EqD`, `EqI`, (future) `Range`, `Renko` |
| `SourceCode` | `1m`, `5m`, `15m`, `1h`, `4h`, `1d`, `ticks` (lowercase, matches existing convention) |
| `Threshold` | positive integer; units defined per type, see §5.4 |

Display name uses colon and SI suffixes: `EqV_1m_1000` ↔ `EqV/1m:1k`, `EqD_1m_1000000` ↔ `EqD/1m:1M`. The UI auto-formats absolute integers into SI for column headers. The sketch's compact form `EqV1m:1k` is supported where horizontal space is tight.

**Partition assignment:** see §5.2. Default monthly granularity per feed; weekly overflow when a month exceeds the size budget.

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

**Existing `candles/<YYYY>-<MM>_<tf>.csv` schema** (confirmed against `PartitionedCsvBarLoader`):

```
ts, o, h, l, c, vol
```

All columns are `long` (Int64 money convention). `ts` is epoch ms. This schema is left untouched.

**New `aggregated/<feedId>/<YYYY>-<MM>.csv` schema** (per §5.2's per-feed-directory layout) — kept aligned with the existing candle schema so the bar reader produces an `Int64Bar` with **no per-type branching**:

```
ts, o, h, l, c, vol
```

Column semantics:

- `ts` — bar **open** time, epoch ms. Mirrors candle convention so `Int64Bar.TimestampMs` semantics are identical for time bars and alt bars: "when did this bar start". Bar duration on alt bars is variable by construction; strategies that compute "time since last bar" will see variable spacing — expected.
- `o`/`h`/`l`/`c` — OHLC of the bar.
  - From `1m`/`1h`/etc. source: `o` = first source candle's open; `h`/`l` = max/min over source candles; `c` = last source candle's close.
  - From `ticks` source: derived per-tick.
- `vol` — total base-asset volume summed over the bar's input. **Always ≥ 0** — invariant matches time bars. The sign-of-volume encoding for direction is rejected (see §10.4); flow data lives in the sidecar.

**Why this minimal schema:** alt bars are consumed as `Int64Bar` by every strategy and indicator. Persisting extra columns in the bar file would either (a) bloat `Int64Bar` for every consumer, or (b) require per-bar-type readers and out-of-band metadata. Keeping bar files schema-identical to `candles/*.csv` means the existing `PartitionedCsvBarLoader` can serve alt bars with only a path-resolution change.

**Sidecar feed schema** (`<feedId>.flow/<YYYY>-<MM>.csv`) — written only when the source has the underlying data; published as a regular `FeedSeries`:

```
ts, signed_imbalance, buy_volume, sell_volume, realized_threshold, bar_index
```

Column semantics:

- `ts` — matches the `ts` of the corresponding bar in the primary file (1:1 row alignment by timestamp; the engine joins via `IFeedContext.TryGetLatest` which already supports timestamp-aligned lookup).
- `signed_imbalance` — populated for EqI only; signed accumulator value at close. Empty for EqV/EqD/EqT.
- `buy_volume`, `sell_volume` — split in base-asset units.
  - From `1m`-class source: `buy_volume = sum(taker_buy_base_volume)`, `sell_volume = vol − buy_volume`.
  - From `ticks` source: per-tick from `isBuyerMaker`.
  - Empty if source feed lacks the data; the sidecar file is omitted entirely if no flow column has data.
- `realized_threshold` — actual accumulator value at bar close (≥ nominal threshold; difference is per-bar overshoot). Useful for diagnostic strategies and overshoot analysis.
- `bar_index` — monotonically increasing across partitions; lets a flow-aware strategy reference bars by index without re-deriving from timestamp.

**v1 minimum:** EqI feeds MUST publish a sidecar with at minimum `signed_imbalance`. Other types MAY publish a sidecar (recommended when source provides taker-buy split, since the data is free at aggregation time). Strategies that don't subscribe to the sidecar are unaffected — `Int64Bar` reads are identical.

Header row is included in every partition file (both bar files and sidecar files). Empty cells in the sidecar are written as empty string (not `0`, to distinguish "unavailable" from "exactly zero"). Bar files have no nullable columns — every cell is a non-negative `long`.

### 5.6 Tick storage schema (locked, Phase 2 prerequisite)

Tick data lives under a sibling `ticks/` directory with **daily** partitions:

```
History/
└── binance/
    └── BTCUSDT_perp/
        └── ticks/
            ├── 2024-01-01.csv
            ├── 2024-01-02.csv
            └── ...
```

**Path:** `<storage_root>/{exchange}/{asset}/ticks/<YYYY>-<MM>-<DD>.csv`

**CSV header (frozen):**

```
ts,price,qty,is_buyer_maker,agg_id
```

| Column | Type | Semantics |
|---|---|---|
| `ts` | `long` | Trade time, epoch ms. Strictly monotonically increasing within a partition; partition assignment by UTC date of `ts`. |
| `price` | `long` | Trade price, tick-denominated per Int64 Money Convention. |
| `qty` | `long` | Trade size in base-asset units (long-scaled per Int64 Money Convention). |
| `is_buyer_maker` | `int` (0/1) | Mirrors Binance's `isBuyerMaker` flag. `1` = trade was a sell-aggressor (counterparty is the buyer-as-maker); `0` = trade was a buy-aggressor. EqI accumulator adds `+qty` for `0`, subtracts `qty` for `1`. |
| `agg_id` | `long` | Exchange aggregate-trade ID. Used for resume/dedup if ingestion crashes mid-partition. |

**Why daily, not monthly:** BTCUSDT perp does ~300k–1M trades/day → a monthly file is 10–30M rows, 500MB–1.5GB. Cold-cache chronological reads and crash-resume semantics both degrade at that size. Daily files are ~50MB, ~30/month — readahead-friendly and finely resumable. The asymmetry vs. candles (monthly) is intentional: candles are sparse, ticks are dense.

**Why this schema is the v1 minimum:** the four columns `ts/price/qty/is_buyer_maker` are exactly what every Phase-2 accumulator (`EqT`, `EqV`, `EqD`, `EqI`) consumes. `agg_id` is added because resume-on-crash is essential for multi-day backfills; omitting it would force re-reading the partition from `ts=0` to find the resume point.

**Header presence:** kept (50 bytes/file is noise compared to 50MB partitions, and matches the existing `PartitionedCsvBarLoader` reader contract).

This schema MUST be implemented as written before Phase 2 aggregator work begins — accumulators embed assumptions about column order via the `SourceRecord` shape (§9.2).

## 6. Feed Metadata in `feeds.json`

There is **no separate per-feed `status.json`** scheme. Aggregated-feed metadata is registered as additional entries in the existing per-asset `feeds.json` (read by `FeedSchemaManager` / `FeedContextBuilder`). This keeps a single source of truth for feed discovery — the same file the engine already loads to wire side feeds.

`feeds.json` gains two new feed-entry kinds: `aggregated` (the bar feed) and (when applicable) the regular `side` entry for the sidecar. Time-bar and tick feeds are still implicit (covered by the existing `candles` block and a future `ticks` block); their first/last timestamps are derived on the fly by partition scan when the API needs them (§7.2).

```json
{
  "candles": {
    "scaleFactor": 100.0,
    "intervals": ["1m", "1h", "1d"]
  },
  "feeds": {
    "funding_rate": {
      "kind": "side",
      "interval": "8h",
      "columns": ["rate"],
      "autoApply": { "type": "FundingRate", "rateColumn": "rate" }
    },
    "EqV_1m_1000": {
      "kind": "aggregated",
      "type": { "code": "EqV", "name": "EqualVolume" },
      "source": { "feed": "1m", "first_ts": "2019-09-01T00:00:00Z", "last_ts": "2026-04-28T00:00:00Z", "record_count": 3458880 },
      "threshold": { "value": 1000, "unit": "base_asset", "input_mode": "absolute", "convenience_input": null },
      "build": {
        "tool_version": "1.4.0",
        "built_at": "2026-04-28T11:23:51Z",
        "duration_seconds": 38,
        "bar_count": 18421,
        "partitions_written": ["2019-09", "2019-10", "...", "2026-04"],
        "max_partition_size_mb": 100
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
      "last_bar_ts":  "2026-04-27T23:51:06Z",
      "sidecar": null
    },
    "EqI_ticks_500000": {
      "kind": "aggregated",
      "type": { "code": "EqI", "name": "EqualImbalance" },
      "threshold": { "value": 500000, "unit": "quote_asset" },
      "fidelity": { "imbalance_reconstruction_method": "tick_signed" },
      "sidecar": "EqI_ticks_500000.flow"
    },
    "EqI_ticks_500000.flow": {
      "kind": "side",
      "columns": ["signed_imbalance", "buy_volume", "sell_volume", "realized_threshold", "bar_index"],
      "autoApply": null
    }
  }
}
```

Field semantics for aggregated entries:

- `kind: "aggregated"` distinguishes alt-bar feeds from `side` feeds. Bar files are read by the bar loader; side feeds (including `.flow` sidecars) are read by `CsvFeedSeriesLoader`.
- `build.partitions_written` lists each persisted partition as either `"YYYY-MM"` (single monthly file) or `"YYYY-MM-W<n>"` (one of multiple weekly overflow files for that month, see §5.2). Re-readers use this list as authoritative truth for layout — they do not need to introspect the filesystem to discover whether a month is split.
- `build.max_partition_size_mb` records the size budget the writer was configured with at build time (default 100). If the value changes between builds, existing partitions are not retroactively re-split.
- `sidecar` — feed ID of the companion `FeedSeries` (if any). The engine auto-subscribes to it when the strategy declares interest (§10.4); strategies that ignore it pay zero cost.
- `threshold.input_mode` ∈ `{"absolute", "convenience"}`. Resolved value is canonical; convenience input (e.g. "≈1000 1m candles' worth") preserved for traceability.

Fidelity field semantics (unchanged from prior draft):

- `estimated_overshoot_pct` — analytic prediction `100 / (2 * n_factor)` where `n_factor = threshold / median_source_record_value`. Computed *before* aggregation runs and used to warn the user when N is small.
- `actual_overshoot_pct` — measured during aggregation: mean over all bars of `(realized_threshold − threshold) / threshold * 100`.
- `max_overshoot_pct` — worst-case overshoot in the dataset (catches whale-trade artifacts).
- `imbalance_reconstruction_method` — only set for `EqI`; one of `"tick_signed"` (true Lee-Ready / `isBuyerMaker`) or `"m1_taker_buy_proxy"` (approximation; underestimates intra-bar two-way pressure when source is a time bar). UI surfaces a warning for the proxy variant.

For aggregations from `ticks`, the analytic estimate approaches zero (per-tick granularity), and `actual_overshoot_pct` should be ≤ 0.05 % in practice.

**Atomicity:** the aggregator writes partition files first, then updates `feeds.json` last via a write-temp-then-rename. The presence of the `feeds.json` entry is the "feed is complete" marker (replaces the old "status.json presence" rule).

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
      { "id": "1m",                   "kind": "OHLCV_TimeBar", "size": 3458880,    "first_ts": "...", "last_ts": "..." },
      { "id": "1h",                   "kind": "OHLCV_TimeBar", "size": 57648,      "first_ts": "...", "last_ts": "..." },
      { "id": "1d",                   "kind": "OHLCV_TimeBar", "size": 2402,       "first_ts": "...", "last_ts": "..." },
      { "id": "ticks",                "kind": "Tick",          "size": 412009993,  "first_ts": "...", "last_ts": "..." },
      { "id": "EqV_1m_1000",          "kind": "OHLCV_AltBar",  "size": 18421,      "first_ts": "...", "last_ts": "...", "sidecar": null },
      { "id": "EqI_ticks_500000",     "kind": "OHLCV_AltBar",  "size": 6234,       "first_ts": "...", "last_ts": "...", "sidecar": "EqI_ticks_500000.flow" },
      { "id": "EqI_ticks_500000.flow","kind": "Side",          "size": 6234,       "first_ts": "...", "last_ts": "..." },
      { "id": "funding_rate",         "kind": "Side",          "size": 7320,       "first_ts": "...", "last_ts": "..." }
    ]
  }
]
```

`feeds[].kind` drives UI grouping and aggregation eligibility:

- `OHLCV_TimeBar` — eligible source for some aggregations (per §11)
- `OHLCV_AltBar` — already aggregated; **not** a re-aggregation source in v1. May reference a `sidecar` feed ID.
- `Tick` — eligible source for ALL aggregation types
- `Side` — funding rate, open interest, mark/index price, **and `.flow` sidecars** for aggregated feeds; not aggregation-eligible. The UI groups sidecars under their parent alt-bar feed rather than listing them flat.

### 7.2 Feed inspection

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status
```
→ the feed's metadata JSON.
- For aggregated feeds: returns the corresponding entry from `feeds.json` (§6).
- For time bars, ticks, and side feeds: generated on the fly by scanning CSV partitions (first/last partition month, total record count, data range), conforming to a minimal schema variant.

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
  "feeds_json_path": "binance/BTCUSDT_perp/feeds.json",
  "sidecar_feed_id": null,
  "fidelity": { "actual_overshoot_pct": 2.42, "max_overshoot_pct": 18.7, "estimated_overshoot_pct": 2.5 },
  "duration_seconds": 38,
  "bar_count": 18421,
  "partitions_written": 80
}
```

For EqI (or any aggregation that produces a sidecar), `sidecar_feed_id` is populated (e.g. `"EqI_ticks_500000.flow"`) so the UI can link to it without parsing.

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

### 8.1 Why a proxy on the main API and not direct FE→HistoryLoader

Three options were considered:

| Option | Verdict |
|---|---|
| Frontend calls HistoryLoader directly | **Rejected.** Splits auth/CORS across two services, leaks HistoryLoader's host into the FE, bypasses the existing rate-limiting/auth pipeline, breaks if HistoryLoader is moved behind a private network. |
| Dedicated reverse-proxy product (YARP / nginx / dedicated gateway service) | **Rejected for v1.** Overkill for ~6 endpoints, adds a deployment unit, and the main `WebApi` already aggregates 8 unrelated route groups (`/api/backtests`, `/api/optimizations`, `/api/live`, etc.) so playing this role for `/api/data` is consistent. Reach for YARP only when hosting multiple HistoryLoader instances or requiring real reverse-proxy features (header rewriting, sticky sessions). |
| **Mirror HistoryLoader endpoints under `/api/data` on the main `WebApi` (chosen).** | Single auth pipeline, single CORS origin, single FE base URL, FE never learns HistoryLoader's host, ~150 LOC of typed `HttpClient` + DTOs. |

### 8.2 Mirror contract

All HistoryLoader endpoints in §7 are mirrored under `/api/data/...` on the main `WebApi` with identical request/response contracts. The main API:

- Forwards each request to the configured HistoryLoader instance via `IHttpClientFactory` + a typed `HistoryLoaderClient` (registered in DI, configured via `IOptions<HistoryLoaderOptions> { BaseUrl, RequestTimeout }`). Pattern mirrors the existing `CandleStorageOptions` config.
- Adds AuthN / AuthZ (existing pipeline) before forwarding.
- Caches `GET /api/data/exchanges` and `GET /api/data/exchanges/{exchange}/assets` (§7.1) with a short TTL (5 s). Cache key: `(exchange, asset)` for the assets endpoint; cache invalidation is **event-driven** — the proxy's `POST /api/data/.../aggregate` and `DELETE /api/data/.../feeds/{id}` handlers clear the affected key on success rather than waiting for TTL.
- Translates HistoryLoader 5xx into structured `ProblemDetails` responses with stable error codes.
- Exposes a single `MapDataEndpoints()` extension following the existing minimal-API pattern in `Program.cs`.

### 8.3 Long-running aggregation: SSE-with-final-summary as primary, sync as opt-in convenience

The §9.1 60-minute keepalive *can* be configured, but binds an entire aggregation to a single TCP connection — fragile, and it fights .NET / reverse-proxy idle defaults (Kestrel's `RequestHeadersTimeout` is 30 s; typical reverse-proxy idle timeouts are 60–120 s). Inverting the original "sync now, SSE later" plan: **SSE-with-final-summary is the primary contract; long-hold sync is an opt-in convenience** for short jobs that callers want to await synchronously.

```
POST /api/data/exchanges/{exchange}/assets/{asset}/aggregate
     → 202 Accepted, body `{ "jobId": "..." }`, header `Location: /api/data/aggregations/{jobId}/progress`
GET  /api/data/aggregations/{jobId}/progress       (Server-Sent Events stream; final event carries the §7.4 summary)
GET  /api/data/aggregations/{jobId}                 (synchronous final-summary fetch; 200 if complete, 202 if still running)
```

**Convenience sync mode.** When the client sends `Prefer: wait=N` (RFC 7240; `N` ≤ 3600 seconds), the proxy holds the request open until completion or `N`-second timeout, then returns the §7.4 summary as a 200 — equivalent to the old sync POST but explicit about the long hold. Clients that omit the header get the 202+SSE flow.

**Why this inversion.** SSE works behind every reverse proxy that supports HTTP/1.1 chunked transfer encoding; long-hold sync POST does not — the 60-minute Kestrel keepalive (§9.1) is necessary but not sufficient for the sync path because intermediate proxies impose their own idle limits. The 60-min Kestrel config is retained, but only `Prefer: wait=...` requests actually consume it; the default path is short-lived requests + a long-running SSE stream. Designing this in now costs the same ~150 LOC as the original sync-only proxy and avoids a v2 retrofit once clients depend on the simpler sync semantics.

### 8.4 Frontend invariant

The FE talks **only** to the main `WebApi` — never directly to HistoryLoader, including in development. This preserves a single CORS origin, a single auth surface, a single observability pipeline, and lets the proxy add cross-cutting concerns (rate limiting, audit logging, request shaping) without FE changes.

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
    │                                    aggregated/<feedId>/<YYYY>-<MM>.csv
    │                                    (or <YYYY>-<MM>-W<n>.csv on overflow)
    │                                    by ts_open; rolls files at month/week boundary;
    │                                    writes header on each new partition;
    │                                    tracks running byte count and splits to weekly
    │                                    overflow when monthly partition exceeds budget)
    ▼
FeedsJsonFinalizer                      (writes/updates feeds.json entry for the feed,
                                         including build.partitions_written and fidelity)
```

Properties:

- **Memory bounded:** O(1) per accumulator, O(write_buffer_size) per partition. Independent of dataset size.
- **Pipelined:** stages connected via `System.Threading.Channels` so disk reads, accumulation, and writes overlap.
- **Batched I/O:** reader yields rows in chunks of 10 k; sink flushes every 5 k bars per partition.
- **Atomic per-partition writes:** each output partition is written to `<filename>.tmp` and atomically renamed on completion of the corresponding month/week boundary. The `feeds.json` entry is written last via write-temp-then-rename; its presence is the "feed is complete" marker (replaces the old "status.json presence" rule).
- **Size-budget overflow:** the writer tracks bytes-written-so-far for the active partition; on exceeding `aggregator.maxPartitionSizeMB` (default 100), it closes the current monthly file and reopens as `<YYYY>-<MM>-W<n>.csv` for the current ISO week-of-month. The split decision is sticky for the remainder of the month — once split, all subsequent bars in that month also route to weekly files, so a month is either entirely-monthly or entirely-weekly (never mixed). See §5.2.
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
Track running mean and max. Write both to the aggregated-feed entry in `feeds.json` at finalization, alongside the analytic estimate computed before the run starts.

## 10. AlgoTradeForge API: DataSubscription Redesign

The current `DataSubscription` is replaced by a polymorphic hierarchy. No backward-compat shims; existing callers are migrated.

### 10.0 `TimeFrame` value type (new prerequisite)

The polymorphic subscription hierarchy below references `TimeFrame.Code` for `TimeBarSubscription.FeedId`. The current codebase has **no `TimeFrame` type** — timeframe is represented as raw `TimeSpan` everywhere (`DataSubscription.TimeFrame: TimeSpan` at `Domain/Strategy/DataSubscription.cs:3`, `IInt64BarLoader.Load(..., TimeSpan interval)`), with formatting routed through the static `TimeFrameFormatter.Format(TimeSpan)`. Phase 1 introduces a small value type to give the new subscription hierarchy a typed identity:

```csharp
public readonly record struct TimeFrame(TimeSpan Duration)
{
    public string Code => TimeFrameFormatter.Format(Duration);

    public static TimeFrame Parse(string code) =>
        TimeFrameFormatter.TryParseShorthand(code, out var ts)
            ? new TimeFrame(ts)
            : throw new FormatException($"Invalid TimeFrame code: '{code}'");
}
```

This is intentionally minimal — it wraps `TimeSpan` rather than replacing it, so existing call sites that take `TimeSpan` keep compiling. Migration is gradual: Phase 1 introduces the type and migrates `DataSubscription`/`IInt64BarLoader`/`PartitionedCsvBarLoader`; Phase 4 removes the raw-`TimeSpan` overloads from public surfaces.

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
| AltBar   | `<storage_root>/{Exchange}/{Asset}/aggregated/{FeedId}/*.csv` |
| Tick     | `<storage_root>/{Exchange}/{Asset}/ticks/*.csv` |
| Side     | `<storage_root>/{Exchange}/{Asset}/{FeedId}/*.csv` (existing convention; `.flow` sidecars route through this same path) |

(Note: TimeBar uses underscore-separated `YYYY-MM_{tf}.csv` per existing `PartitionedCsvBarLoader.GetPartitionPath`. AltBar uses a per-feed subdirectory containing `YYYY-MM.csv` partitions, with optional `YYYY-MM-W<n>.csv` weekly overflow files when a month exceeds the size budget — see §5.2. The same `*.csv` glob covers both forms; lexicographic sort agrees with chronological order.)

Partitions are read in chronological order via a single CSV reader; the engine then consumes `Int64Bar` records one by one regardless of whether they came from `candles/` or `aggregated/`. The bar CSV schema is **identical** for both (`ts, o, h, l, c, vol` — see §5.5), so the existing `PartitionedCsvBarLoader` serves both with only path-resolution branching — no per-bar-type reader, no per-bar-type bar struct.

### 10.3 `Int64Bar` consumer compatibility

Alt bars share the existing `Int64Bar` shape with time bars:

```csharp
public readonly record struct Int64Bar(
    long TimestampMs, long Open, long High, long Low, long Close, long Volume);
```

This is deliberate. Strategies, indicators, exporters, charts, and reporting all consume `Int64Bar` — none of them need to know how a bar was produced. An EqV breakout strategy, an MA crossover, an ATR indicator: all see a stream of `Int64Bar` and behave correctly whether each bar represents 1 minute of wall time or 1000 BTC of cumulative volume.

**Universal invariants on `Int64Bar` (preserved for both time bars and alt bars):**

| Field | Semantics on time bars | Semantics on alt bars | Invariant |
|---|---|---|---|
| `TimestampMs` | Bar **open** (epoch ms) | Bar **open** (epoch ms) | Strictly monotonically increasing within a feed |
| `Open` / `High` / `Low` / `Close` | OHLC over fixed time window | OHLC over fixed accumulator window | `Low ≤ Open, Close ≤ High`; all ≥ 0 |
| `Volume` | Total base-asset volume in the bar | Total base-asset volume in the bar | **Always ≥ 0** |

The `Volume ≥ 0` invariant is preserved across all alt-bar types — including EqI. Sign-of-volume encoding for direction is **rejected** (would silently break every existing volume-using indicator). Bit-packing buy/sell into one `long` is also **rejected** (range-unsafe for `int32` halves on crypto volumes; introduces context-dependent meaning of a primitive type).

`Int64Bar`'s six-`long` shape is exactly 48 bytes — cache-line friendly, plugin-ABI stable, and unchanged by this feature. Strategy plugins (public and private repo) compile against the same `Int64Bar` they always have.

### 10.4 Flow data (sidecar feed access)

Bars carry no flow information. Strategies that need imbalance, buy/sell split, or per-bar overshoot subscribe to the bar's sidecar `FeedSeries` via a typed accessor on `IFeedContext` — **not** via a magic string key. This eliminates collision risk with user-named feeds in `feeds.json` and keeps the strategy code decoupled from the alt-bar's specific feed ID.

```csharp
public interface IFeedContext
{
    // Existing members (unchanged — note `out double[]`, NOT `ReadOnlySpan<double>`;
    // see Domain/Strategy/IFeedContext.cs:16. The returned array is a shared buffer
    // that strategies must NOT hold across bars.)
    bool TryGetLatest(string feedKey, out double[] values);
    bool HasNewData(string feedKey);
    DataFeedSchema GetSchema(string feedKey);

    // New (Phase 2b): typed accessor for the primary bar's sidecar feed, if any.
    // Shipped as default-interface-method (DIM) implementations so existing
    // plugin strategies that implement IFeedContext keep satisfying the
    // interface without recompilation. Phase 1 includes a DIM-compatibility
    // audit (§14) before this lands; the fallback if DIMs prove unsafe is a
    // separate opt-in `ISidecarReceiver` interface registered alongside
    // `IFeedContextReceiver`, leaving `IFeedContext` itself unchanged.
    // Returns false when the primary is not an alt bar OR has no sidecar declared in feeds.json.
    bool TryGetPrimarySidecar(out double[] values) { values = []; return false; }

    // Static schema descriptor so strategies can validate column layout at init.
    PrimarySidecarSchema? PrimarySidecarSchema => null;
}

public sealed record PrimarySidecarSchema(IReadOnlyList<string> Columns);
```

Usage from a strategy:

```csharp
public sealed class MyEqIStrategy : IInt64BarStrategy, IFeedContextReceiver
{
    private IFeedContext? _ctx;
    private int _imbalanceCol = -1;

    public void OnFeedContext(IFeedContext ctx)
    {
        _ctx = ctx;
        _imbalanceCol = ctx.PrimarySidecarSchema?.Columns.IndexOf("signed_imbalance") ?? -1;
    }

    public void OnBar(in Int64Bar bar)
    {
        // OHLCV consumption is identical to a time-bar strategy.
        if (_imbalanceCol >= 0 && _ctx!.TryGetPrimarySidecar(out var values))
        {
            var signedImbalance = values[_imbalanceCol];
            if (double.IsNaN(signedImbalance)) return; // sidecar column unavailable for this bar; see §15-Q8
            // ... use signed flow data
        }
    }
}
```

**Why a typed accessor instead of a synthetic string key:** any string-based scheme (`"primary.flow"`, `"sidecar"`, etc.) requires (a) reserving a namespace in `feeds.json` to prevent user-named feeds from colliding, (b) documenting that reservation, and (c) trusting that nobody ignores the documentation. A typed accessor removes the string from the API entirely — there's nothing for users to clash with. This matches the codebase's existing pattern of typed init via `IFeedContextReceiver` over loose string lookup.

The engine binds `TryGetPrimarySidecar` to the `FeedSeries` referenced by the primary alt bar's `sidecar` field in `feeds.json` (§6). For `TimeBarSubscription` primaries (or `AltBarSubscription` primaries with `sidecar: null`), the accessor returns `false` and `PrimarySidecarSchema` is `null`. Strategies that don't call it pay zero cost — the sidecar is loaded only when at least one consumer's init resolves a non-null schema, mirroring the existing side-feed lazy-load behavior.

Side feeds named in `feeds.json` continue to be reachable through the existing `TryGetLatest(feedKey, ...)` API — this remains the right mechanism for funding rate, open interest, and other user-declared feeds.

**v1 sidecar schema:** EqI sidecars MUST publish `signed_imbalance`. Other types MAY publish a sidecar (recommended when source provides taker-buy split). Column order is fixed per §5.5 so strategies can index by name via `PrimarySidecarSchema.Columns.IndexOf(...)`.

### 10.5 Loader signature change

The current `IInt64BarLoader` (`Application/CandleIngestion/IInt64BarLoader.cs`) takes `TimeSpan interval`:

```csharp
TimeSeries<Int64Bar> Load(string dataRoot, string exchange, string symbol,
    DateOnly from, DateOnly to, TimeSpan interval);
```

Alt bars have no fixed interval, so this signature must change. Phase 1 introduces:

```csharp
TimeSeries<Int64Bar> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to);

public readonly record struct DataFeedDescriptor(
    string DataRoot, string Exchange, string Asset, string FeedId, DataFeedKind Kind);
```

The existing `PartitionedCsvBarLoader` gets a `Kind`-based path resolver (TimeBar → `candles/<YYYY-MM-FeedId>.csv`, AltBar → `aggregated/<FeedId>/<YYYY-MM>.csv` with `*.csv` glob to also pick up `<YYYY-MM>-W<n>.csv` weekly overflow files per §5.2). All call sites (`HistoryRepository.Load`, `BacktestPreparer`, ingestion paths, tests) are updated in the same Phase-1 commit. **Migration inventory** (auditable in advance):

- `src/AlgoTradeForge.Application/CandleIngestion/IInt64BarLoader.cs` (interface)
- `src/AlgoTradeForge.Infrastructure/History/PartitionedCsvBarLoader.cs` (impl)
- `src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvInt64BarLoader.cs` (legacy impl — evaluate for deletion)
- `src/AlgoTradeForge.Infrastructure/Persistence/HistoryRepository.cs` (caller)
- `src/AlgoTradeForge.Application/Backtests/BacktestPreparer.cs:34` (caller — primary bar selection is **today positional**: `command.DataSubscriptions[0]`. The Phase 4 redesign makes primary an explicit field, not list[0]; the index-based contract must be removed in the same commit that introduces `BacktestInputs.Primary`.)
- `src/AlgoTradeForge.Domain/Strategy/DataSubscription.cs` (the existing `DataSubscription.FeedKey` defaults to the literal `"ohlcv"`. Grep the solution for `"ohlcv"` literals before deleting the type — any matches need explicit migration to the new subscription hierarchy or to be moved into a constants class.)
- All tests under `tests/AlgoTradeForge.{Application,Infrastructure}.Tests/` referencing the loader
- Private repo: any custom strategy factory that builds subscriptions

This is a Phase-1 breaking change, not Phase 4. Doing it early avoids forcing the Phase-4 DataSubscription redesign to refactor half the Application layer.

Side feeds are exposed to strategies through the existing side-feed API; aggregated bars used as side inputs travel the same path.

### 10.6 Optimization, validation, debug

- **Backtest / Debug:** single primary, list of side feeds.
- **Optimization:** `BacktestInputs.Primary` is replaced by `IReadOnlyList<DataFeedSubscription> PrimaryCandidates` in `OptimizationInputs`; the engine fans out across primaries × parameter grid.
- **Validation (walk-forward / OOS):** uses the same primary feed as backtest; range is split server-side.

## 11. Compatibility Matrix (source feed → eligible bar types)

| Source kind | Eligible types | Notes |
|-------------|----------------|-------|
| Tick | EqT, EqV, EqD, EqI, (future) Range, Renko | All types; highest fidelity. |
| OHLCV_TimeBar with `number_of_trades`, `volume`, `quote_volume`, `taker_buy_*` (Binance klines all have these) | EqT, EqV, EqD, EqI* | EqI* persisted with `imbalance_reconstruction_method = "m1_taker_buy_proxy"` and a UI fidelity warning (§Q2 resolved). |
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
- Cell content: `+` if feed exists for that asset, `−` otherwise. Aggregated cells with a sidecar render an extra dot indicator linking to the sidecar's row (or expand-on-click).
- `+` is clickable → opens the right sidebar with that feed's metadata (read-only Monaco editor showing the `feeds.json` entry from §6, or a partition-derived minimal status for time bars / ticks per §7.2).
- Asset row label is clickable → also opens sidebar, with the "New aggregate bar" form pre-populated for that asset (no source pre-selected).

### 12.2 Right sidebar — Status

Two stacked cards:

**Status card** — shows the active feed's metadata entry from `feeds.json` (or partition-scan-derived status for time bars / ticks), read-only Monaco editor with JSON syntax highlighting and folding.

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
- **EqI fidelity warning (§Q2 resolved):** when Source is `OHLCV_TimeBar` and Type is `EqI`, the form renders a yellow warning banner: *"Imbalance built from time bars uses the taker-buy proxy and underestimates intra-bar churn. Sign-of-imbalance strategies are unaffected; magnitude-sensitive strategies should rebuild from `ticks` when available."* The Aggregate button is **enabled** (the proxy is allowed); the warning is informational. Once the feed is built, its `feeds.json` entry shows `imbalance_reconstruction_method: "m1_taker_buy_proxy"` and the Status card surfaces the same banner so downstream backtest runs inherit the warning visibility. Tick-source EqI shows `"tick_signed"` and no warning.

### 12.4 Optional column-ordering enhancement

Within columns, "aggregation-eligible" feeds (`Tick`, `OHLCV_TimeBar`) render with an icon prefix and are pulled left within their group. Pure consumers (alt bars) render plain. Side feeds render dimmed and right.

## 13. UI: Backtest / Optimization Launch

- **Primary feed selection:** replaces the current implicit `DataSubscription` form with an explicit "Primary feed" dropdown populated from `/api/data/exchanges/{exchange}/assets/{asset}/feeds`. Display label is the friendly form (`EqV/1m:1k`, `1m`, `ticks`) with iconography distinguishing time bars from alt bars.
- **Side feeds:** existing multi-select, now able to include alt bars too.
- **Optimization:** the Primary dropdown becomes a multi-select chip control; the engine fans out across selected primaries × parameter grid. UI shows estimated run count (`primaries × param_combos`).

## 14. Phased Rollout

0. **Phase 0 — Pre-flight.** Q1 (CSV schema — corrected: underscore-separated `YYYY-MM_<tf>.csv`, not dash-joined), Q2 (EqI proxy stance), Q4 (tick storage), Q8 (sidecar `NaN` sentinel), Q9 (sidecar key) all **resolved** in this revision. Remaining §15 items are non-blocking: Q3 (convenience-input UX), Q5 (per-feed mutex — design noted, implement in Phase 1), Q6 (resumable aggregations — v2 deferral confirmed), Q7 (multi-instance HistoryLoader — v1 single-instance), Q10 (`IFeedContextReceiver` plugin ABI audit — DIM strategy on `IFeedContext` covers it; verify in Phase 1's DIM audit task), Q11 (`IInt64BarLoader` external-consumer audit). Run Q10 + Q11 audits before Phase 1.

**Codebase corrections also folded into this revision** (per the gap analysis against the current `main` branch):
   - `IFeedContext.TryGetLatest` actually uses `out double[] values`, not `out ReadOnlySpan<double>` — code samples in §10.4 corrected.
   - No `TimeFrame` value type exists today; timeframe is raw `TimeSpan`. §10.0 introduces a minimal `TimeFrame` wrapper.
   - `BacktestPreparer.cs:34` selects primary as `command.DataSubscriptions[0]` (positional). §10.5 migration inventory now flags this.
   - `DataSubscription.FeedKey` defaults to the literal `"ohlcv"`. §10.5 migration inventory now requires a grep before deletion.
   - HistoryLoader.WebApi has no `/aggregate` or feed-mutating endpoints today — Phase 1 covers this greenfield surface explicitly.
   - `PartitionedCsvBarLoader` does not glob — Phase 1 partition-listing task covers the migration.
1. **Phase 1 — Storage & HistoryLoader core + loader signature change.** Storage convention, accumulators (`EqT`, `EqV`, `EqD`), `feeds.json` extension with fidelity, **per-`feed_id` aggregation mutex with 423 Locked semantics** (§7.4). Time-bar source only (`1m`/`1h`/`1d`). **Includes the `IInt64BarLoader` signature change to `DataFeedDescriptor`-based loading** (§10.5) so Phase 4 isn't a forced refactor of half the Application layer. Phase 1 also covers these explicitly-greenfield items that the original draft folded into "core":
   - **Partition-listing strategy for `PartitionedCsvBarLoader`.** The current loader iterates explicit month paths (`PartitionedCsvBarLoader.cs:29-35`) and cannot serve `aggregated/<feedId>/<YYYY>-<MM>.csv` plus optional `<YYYY>-<MM>-W<n>.csv` weekly overflow files (§5.2). Decide between (a) glob-based listing per directory (cleanest; minor behavior change for the existing time-bar path — needs regression tests on out-of-order files) or (b) `Kind`-aware path resolver with two filename templates per month (preserves time-bar behavior, more conditional code). Recommendation: (a), gated on the regression suite.
   - **DIM audit on `IFeedContext` additions.** Validate that the default-interface-method implementations of `TryGetPrimarySidecar` (returning `(false, [])`) and `PrimarySidecarSchema` (returning `null`) allow private-repo strategy plugins to compile and load against the updated `IFeedContext` *without recompilation*. The audit must cover (i) `PluginLoader.cs` `AssemblyLoadContext.Default` semantics on .NET 10, (ii) every `IFeedContext`-implementing class in the public + private repos, (iii) the JIT path that resolves DIMs at first call. If DIMs prove unsafe for the plugin ABI, fall back to a separate opt-in interface (`ISidecarReceiver`) registered alongside `IFeedContextReceiver` rather than extending `IFeedContext`.
   - **HistoryLoader.WebApi `/api/v1/aggregate` is greenfield.** Today the WebApi exposes only `/api/v1/backfill` (`BackfillEndpoints.cs`), `/api/v1/status` (`StatusEndpoints.cs`), and `/health`. Phase 1 must include endpoint wiring for `/aggregate` + `/feeds/{id}` (DELETE), per-`feed_id` mutex (§Q5), the long-hold response handling (`Prefer: wait=N`, see §8.3 — Phase 1 ships the 202+SSE primary contract; the convenience sync mode lands with the proxy in Phase 3), the X-Job-Id response header for SSE pairing, and an HTTP-driven `feeds.json` writer hook (today `FeedSchemaManager.EnsureSchema` is the only writer and it's worker-internal — needs a service-layer wrapper that the new endpoints can call atomically).
   - **§Q8 `CsvFeedSeriesLoader` NaN-sentinel update.** Today the loader fills missing/empty cells with `0.0` (`CsvFeedSeriesLoader.cs:121`). Phase 1 changes empty-string cells to decode as `double.NaN` while keeping explicit `0` parsing intact, so Phase 2b sidecar reads can distinguish "unavailable" from "exact zero". Existing time-bar feeds have no empty cells, so the change is opt-in by sidecar declaration (no regression on side feeds).
2a. **Phase 2a — Tick infrastructure (greenfield).** Tick ingestion service (writes §5.6 daily partitions), tick CSV writer with `agg_id` resume-on-crash, tick reader (`IInt64TickReader` or extension to `PartitionedCsvBarLoader` consuming the daily `ticks/<YYYY>-<MM>-<DD>.csv` partitions), `feeds.json` registration of `ticks` as an implicit feed kind (§6 schema discriminator update), and a `TickSubscription` resolver in the Phase 4 hierarchy (preview). Aggregator `PartitionedSourceReader` (§9.2) gains a tick-source mode that reads the daily partitions across a date range as a single chronological stream. **No EqI yet** — Phase 2a delivers tick storage + reading only, validated by re-running the existing `EqV`/`EqD`/`EqT` accumulators from the new tick source on at least BTCUSDT_perp × 1y and confirming `actual_overshoot_pct` ≤ 0.05 % (§9.4) versus the time-bar baseline. This split exists because tick storage today is **zero-LOC** (no loaders, no ingestor, no schema registration), and folding it into the EqI phase obscures the actual surface area.

2b. **Phase 2b — EqI + sidecar feed pipeline + `IFeedContext` extension.** Signed accumulator, EqI type with proxy/tick-signed reconstruction-method tagging (§Q2 resolution), `.flow` sidecar `FeedSeries` writer + reader (reuses `CsvFeedSeriesLoader` after the §Q8 NaN-sentinel update from Phase 1), the new `IFeedContext.TryGetPrimarySidecar` + `PrimarySidecarSchema` typed accessor (§10.4) shipped as default-interface methods (Phase 1 DIM audit must complete first; fall back to `ISidecarReceiver` if the audit blocks DIMs). UI fidelity warning surface for time-bar-source EqI (§12.3).
3. **Phase 3 — Main API proxy + SSE progress + Data tab UI.** `/api/data/*` mirror endpoints, typed `HistoryLoaderClient`, event-driven cache invalidation, **SSE progress endpoint shipped from day one** (not deferred — see §8.3), Data tab UI with sidecar grouping.
4. **Phase 4 — DataSubscription redesign + backtest / optimization launch UI integration.** Polymorphic subscription hierarchy (§10), `BacktestInputs.Primary`, optimization fan-out across primary candidates.
5. **Phase 5 — Range / Renko accumulators.** Path-dependent; require ticks or sub-minute time bars.
6. **Phase 6 (if needed) — async/job-queue aggregation** for very long tick jobs that exceed 60 minutes. SSE already present from Phase 3; this phase adds durable job queuing + resumption.

## 15. Open Questions / Assumptions

1. **Existing `candles/*.csv` schema** — **resolved.** Header `ts, o, h, l, c, vol` (all `long`, ts epoch ms), partitioned monthly as `<YYYY>-<MM>-<TimeframeCode>.csv`. Confirmed against `PartitionedCsvBarLoader`. Aggregated bar files use the **same** schema (§5.5) — alt bars are drop-in `Int64Bar` consumers.
2. **EqI from a time-bar source** — **resolved (a): proxy with warning.** Within a single source candle we credit the *net* taker-buy-vs-sell as a single signed contribution; this is directionally correct but underestimates magnitude during high-churn periods (Scenario A `100 buys / 50 sells = +50` is indistinguishable from Scenario B `1000 buys / 950 sells = +50`). Decision: allow EqI from 1m sources, persist `imbalance_reconstruction_method = "m1_taker_buy_proxy"` in `feeds.json`, surface a yellow warning badge in the UI ("Imbalance built from time bars; intra-bar churn underestimated"). Tick-source EqI uses `imbalance_reconstruction_method = "tick_signed"` and shows no warning. Rationale: lets researchers prototype before tick ingestion lands (Phase 2); the bias is labeled and persisted so it's auditable rather than hidden. Strategies that trade on sign-of-imbalance still work; only strategies that depend on imbalance *magnitude* are sensitive to the proxy. See §11 (compatibility matrix marks EqI from time bars as `EqI*` with the warning) and §12.3 (UI surfaces the warning when source is `OHLCV_TimeBar` and type is `EqI`).
3. **Threshold convenience input** — the sketch labels (`EqV1m:1k`, "Equal volume bar built of 1k 1m") could mean either "absolute 1000 base units" or "≈1000 1-minute candles' worth of volume". Recommendation: store absolute, accept either as input mode, surface both in the `feeds.json` aggregated entry (`threshold.input_mode` + `threshold.convenience_input`). Confirm sketch's intent.
4. **Tick storage layout** — **resolved.** Locked schema (see §5.6 below):
   - **Path:** `<storage_root>/{exchange}/{asset}/ticks/<YYYY>-<MM>-<DD>.csv` (daily partitions — tick density makes monthly files too coarse for resume/readahead).
   - **Header:** `ts,price,qty,is_buyer_maker,agg_id` (header row included for parser consistency with candles + side feeds).
   - **Types:** `long, long, long, int (0/1), long`. `price` is tick-denominated per Int64 Money Convention; `qty` is base-asset units; `is_buyer_maker` mirrors Binance's flag (1 = trade was sell-aggressor, 0 = buy-aggressor); `agg_id` is the exchange's aggregate-trade ID, used for resume/dedup.
   - Daily partition granularity differs from candles (monthly) intentionally: a single BTCUSDT perp month is 10–30M ticks (~500MB–1.5GB) which is too coarse for cold-cache reads and resume semantics. Daily files are ~50MB each, ~30 files/month. The asymmetry is the right tradeoff.
5. **Concurrent aggregations** — should the API serialize aggregations per `feed_id` to avoid two writes to the same target? Recommendation: yes, with a per-`feed_id` mutex; concurrent jobs targeting different feeds are allowed. Returns 423 Locked on collision.
6. **Partial / resumable aggregations** — v1 always aggregates the full source range. `from_ts` / `to_ts` are reserved in the API but treated as full-range until v2. Incremental rebuilds (only re-run the last partition when new source data arrives) are a natural extension since partitions are calendar-aligned (monthly, with weekly overflow per §5.2) and files are atomic per partition.
7. **Storage root path discovery** — assumed configurable on HistoryLoader; main API gets it via `IOptions<HistoryLoaderOptions> { BaseUrl, RequestTimeout }`. Multiple HistoryLoader instances per main API? v1 assumes a single instance; YARP is the upgrade path if/when this changes.
8. **Empty cells in sidecar CSVs** — **resolved.** Empty string in a sidecar CSV cell decodes to `double.NaN`; bar-file cells remain non-nullable `long`. `CsvFeedSeriesLoader` today fills missing/empty values with `0.0` (`Infrastructure/History/CsvFeedSeriesLoader.cs:121`), which collapses "unavailable" with "exact zero" — Phase 1 updates the parser to emit `NaN` for empty strings while keeping explicit `0` parsing intact (the change is opt-in by sidecar declaration; existing time-bar feeds have no empty cells, so side-feed readers are unaffected). `FeedSeries.Columns` stays `double[][]` — no schema migration required, since `NaN` fits the existing column type. Strategies that read sidecar columns must guard with `double.IsNaN(v)`; the `IFeedContext.TryGetPrimarySidecar` example in §10.4 shows the pattern. Rejected alternatives: (a) parallel `bool[][]` mask on `FeedSeries` (heavy; adds a column-major allocation per feed that strategies almost never use); (b) omit columns entirely when source lacks data (forces sidecar schemas to be source-dependent — a feed registered as having `signed_imbalance` should always *have* the column even if some rows are unavailable, so the column index resolved at strategy `OnInit` stays stable across the dataset).
9. **Sidecar feed-key namespacing** — **resolved.** Replaced the string-keyed approach with a typed `IFeedContext.TryGetPrimarySidecar(out ReadOnlySpan<double>)` accessor + `PrimarySidecarSchema` descriptor (§10.4). No user-facing string key, no namespace reservation, no collision possible. Side feeds named in `feeds.json` continue to be reachable via `TryGetLatest(feedKey, ...)` as today; the new accessor is exclusively for the primary alt bar's auto-bound sidecar.
10. **Plugin ABI for `IFeedContextReceiver`** — confirm this interface is already public/stable in the Domain layer. Strategies in the private repo must compile against the same interface unchanged for the sidecar pattern to work without breaking releases.
11. **Loader signature migration** — the Phase-1 change from `TimeSpan interval` to `DataFeedDescriptor` (§10.5) is breaking for any external code that takes a dependency on `IInt64BarLoader`. Audit external consumers (private repo, any plugins) before Phase 1 lands.
12. **Aggregated-feed partitioning scheme** — **resolved.** Adopted per-feed-directory layout `aggregated/{feedId}/<YYYY>-<MM>.csv` (replacing the originally proposed flat `aggregated/<YYYY>-<MM>_{feedId}.csv`). Reasons: matches the existing side-feed convention 1:1, eliminates partition-name collision risk entirely, simplifies per-feed delete/discovery, and groups partitions by feed naturally in `ls`. Realistic alt-bar configurations produce <50 MB monthly partitions — comfortably within readahead size. **Pathological-density fallback:** when a monthly partition exceeds the configurable size budget (default 100 MB), the writer splits that month into ISO-week files `<YYYY>-<MM>-W<n>.csv`. The `*.csv` glob covers both forms; `feeds.json` `build.partitions_written` records which months are split so re-readers don't introspect the filesystem. This avoids forcing weekly/daily granularity on the 95% of feeds that don't need it.