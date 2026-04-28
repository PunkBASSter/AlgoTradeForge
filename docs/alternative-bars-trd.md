# TRD: Alternative Bar Aggregations in AlgoTradeForge

**Status:** Draft v2
**Scope:** HistoryLoader + AlgoTradeForge main API + Web UI

---

## 1. Overview

Add first-class support for **information-driven bars** (tick, volume, dollar, imbalance) and reserve a slot for **path-dependent bars** (Range, Renko) in AlgoTradeForge. Bars are produced by aggregating data already stored by HistoryLoader (`1m`/`5m`/`1h`/`1d` OHLCV, or raw ticks). Aggregated feeds are persisted alongside time bars, exposed through HistoryLoader's REST API, listed in a new **Data** tab in the UI, and selectable as **primary** OHLC input for backtest, optimization, validation, and debug runs.

The design takes a clean break with the current `DataSubscription` (no backward compatibility) so the data-feed model scales to any future bar type.

## 2. Goals & Non-Goals

**Goals**
- Persisted alt-bar feeds reusable across runs (no re-aggregation per run).
- Sync streaming, memory-bounded aggregation (seconds for years of `1m`; minutes for tick sources).
- Storage convention that scales to new bar types with no further refactors.
- UI for inspecting feeds and triggering aggregations.
- Per-feed fidelity metadata (overshoot error) so users can judge quality at a glance.

**Non-Goals**
- Real-time streaming aggregation of live ticks (historical only in v1).
- Range/Renko in v1 (slot reserved).
- Cross-exchange composite feeds.
- Migration of existing artifacts.

## 3. Glossary

| Term | Meaning |
|---|---|
| Source feed | Persisted input: `1m`, `5m`, `1h`, `ticks`, … |
| Aggregated feed | Persisted output: an alt-bar series. |
| Threshold (N) | Aggregation parameter (volume/ticks/dollars/imbalance per bar). |
| Overshoot | Per-bar excess over N due to source granularity (the fidelity error). |
| Bar type code | `EqT`, `EqV`, `EqD`, `EqI`, future `Range`, `Renko`. |
| Feed ID | Stable identifier within `(exchange, asset)`; used in API, DataSubscription, partition dir name. |

## 4. Storage Convention

### 4.1 Existing layout

Per-asset data lives in CSVs partitioned monthly. Candles in `candles/` (filename `<YYYY>-<MM>_<tf>.csv`, e.g. `2019-09_1m.csv`); side feeds (funding rate, OI, …) in sibling per-feed directories. Per-asset `feeds.json` declares side-feed schemas.

### 4.2 Aggregated feeds layout (new)

Aggregated bars live under `aggregated/<feedId>/<YYYY>-<MM>.csv`, mirroring the per-feed-directory pattern of side feeds. Sidecar flow data (when present) lives in `<feedId>.flow/<YYYY>-<MM>.csv`.

```
History/binance/BTCUSDT_perp/
├── candles/2019-09_1m.csv, 2019-09_1h.csv, …
├── ticks/2024-01-01.csv, …                   # if/when present (§4.6)
├── funding_rate/2019-09_8h.csv               # existing side feed
├── aggregated/
│   ├── EqV_1m_1000/2019-09.csv, 2019-10.csv, …
│   ├── EqD_1m_1000000/2019-09.csv, …
│   ├── EqT_ticks_5000/2019-09.csv, …
│   └── EqI_ticks_500000/2019-09.csv, …
├── EqI_ticks_500000.flow/2019-09.csv, …      # sidecar FeedSeries
└── feeds.json
```

A bar lands in the partition matching the UTC `YYYY-MM` of its `ts_open`. Multi-month bars (large N, low activity) fall in the open-month file; intervening months may be empty.

**Size cap with weekly overflow.** Writer enforces a soft per-partition byte budget (default **100 MB**, configurable as `aggregator.maxPartitionSizeMB`). When a monthly partition would exceed the budget, the month splits into ISO-week files `<YYYY>-<MM>-W<n>.csv`. The decision is sticky for the rest of the month (a month is entirely-monthly or entirely-weekly, never mixed). The writer pre-opens `-W<n>.csv` from month start if the previous month overflowed.

Reader globs `aggregated/<feedId>/*.csv`; lexicographic sort agrees with calendar order across both forms. `feeds.json` `build.partitions_written` records per-month layout so re-readers don't introspect the filesystem.

### 4.3 Naming grammar & feed ID

```
<TypeCode>_<SourceCode>_<Threshold>/<YYYY>-<MM>[-W<n>].csv          # bar file
<TypeCode>_<SourceCode>_<Threshold>.flow/<YYYY>-<MM>[-W<n>].csv     # sidecar (when published)
```

Feed ID is the directory name: `EqV_1m_1000`. Sidecar feed ID is `<feedId>.flow`.

| Field | Values |
|---|---|
| `TypeCode` | `EqT`, `EqV`, `EqD`, `EqI`, future `Range`, `Renko` |
| `SourceCode` | `1m`, `5m`, `15m`, `1h`, `4h`, `1d`, `ticks` |
| `Threshold` | positive integer; units per §4.4 |

Display name uses SI: `EqV_1m_1000` ↔ `EqV/1m:1k`.

### 4.4 Threshold units per type

| TypeCode | Threshold meaning | Unit |
|---|---|---|
| EqT | Trade/tick count per bar | trades |
| EqV | Cumulative base-asset volume per bar | base asset units |
| EqD | Cumulative quote-asset volume per bar | quote currency units |
| EqI | Absolute cumulative signed quote volume per bar | quote currency units |
| Range / Renko (future) | Price travel / brick size | price points |

Threshold is always stored as an absolute integer in the filename. Convenience input modes (e.g. "≈1000 1m candles' worth") are resolved at the UI/API boundary and preserved in `feeds.json` for traceability.

**Scale alignment** (open item — see §13 Q1): for `EqV` / `EqI`-base, the source `qty` value and the threshold N must use the same scale. Phase 2 must pin the canonical scale (likely the asset's quantity-step factor) and apply it consistently in tick CSV writes (§4.6) and accumulator comparisons (§7.3).

### 4.5 CSV schemas

**Candle (existing, unchanged):**
```
ts, o, h, l, c, vol            # all long; ts = epoch ms
```

**Aggregated bar (new) — same schema as candles:**
```
ts, o, h, l, c, vol
```

- `ts` = bar **open** time (epoch ms). Bar duration is variable on alt bars by construction.
- OHLC: from time-bar source — first open, max high, min low, last close. From ticks — derived per-tick.
- `vol` ≥ 0 always. Sign-of-volume direction encoding is **rejected** (would break every existing volume-using indicator); flow data lives in the sidecar.

The identical schema means the existing `PartitionedCsvBarLoader` serves alt bars with only path-resolution branching — no per-type reader, no per-type bar struct.

**Sidecar (`<feedId>.flow/<YYYY>-<MM>.csv`)** — published when the source has the data:
```
ts, signed_imbalance, buy_volume, sell_volume, realized_threshold, bar_index
```

- `ts` = matches the corresponding bar's `ts` (1:1).
- `signed_imbalance` populated for EqI only; empty otherwise.
- `buy_volume` / `sell_volume` from `taker_buy_*` (time-bar source) or `is_buyer_maker` (tick source); empty if source lacks the data.
- `realized_threshold` = actual accumulator at close (≥ N; difference is per-bar overshoot).
- `bar_index` = monotonic across partitions; **feed-build-scoped**, not stable across rebuilds.

**v1 minimum:** EqI feeds MUST publish a sidecar with at least `signed_imbalance`. Other types MAY publish one when source provides taker-buy split. Empty cells decode to `double.NaN` (§13 Q3); bar-file cells are non-nullable `long`. Header row included in every partition.

### 4.6 Tick storage schema (Phase 2 prerequisite)

```
History/binance/BTCUSDT_perp/ticks/<YYYY>-<MM>-<DD>.csv        # daily partitions
```

Header (frozen):
```
ts,price,qty,is_buyer_maker,agg_id
```

| Column | Type | Semantics |
|---|---|---|
| `ts` | long | Trade time, epoch ms; strictly monotonic within partition; UTC date routes partition. |
| `price` | long | Tick-denominated per Int64 Money Convention. |
| `qty` | long | Base-asset quantity, scaled per §4.4 alignment rule. |
| `is_buyer_maker` | int 0/1 | Mirrors Binance's flag. `1` = sell-aggressor; `0` = buy-aggressor. EqI: +qty for `0`, −qty for `1`. |
| `agg_id` | long | Exchange aggregate-trade ID; resume-on-crash key. |

**Why daily, not monthly:** BTCUSDT perp does ~300k–1M trades/day → monthly file would be 500MB–1.5GB. Daily files are ~50MB, ~30/month — readahead-friendly and finely resumable.

This schema MUST ship before Phase 2 aggregator work; accumulators embed assumptions about column order via the `SourceRecord` shape.

## 5. Feed Metadata in `feeds.json`

No separate per-feed `status.json`. Aggregated metadata extends the existing per-asset `feeds.json` (single source of truth, already loaded by `FeedSchemaManager` / `FeedContextBuilder`).

```json
{
  "candles": { "scaleFactor": 100.0, "intervals": ["1m", "1h", "1d"] },
  "feeds": {
    "funding_rate": {
      "kind": "side", "interval": "8h", "columns": ["rate"],
      "autoApply": { "type": "FundingRate", "rateColumn": "rate" }
    },
    "EqV_1m_1000": {
      "kind": "aggregated",
      "type": { "code": "EqV", "name": "EqualVolume" },
      "source": { "feed": "1m", "first_ts": "...", "last_ts": "...", "record_count": 3458880 },
      "threshold": { "value": 1000, "unit": "base_asset", "input_mode": "absolute", "convenience_input": null },
      "build": {
        "tool_version": "1.4.0", "built_at": "...", "duration_seconds": 38,
        "bar_count": 18421,
        "partitions_written": ["2019-09", "2019-10", "...", "2026-04"],
        "max_partition_size_mb": 100
      },
      "fidelity": {
        "estimated_overshoot_pct": 2.5, "actual_overshoot_pct": 2.42,
        "max_overshoot_pct": 18.7, "median_source_record_value": 0.5,
        "n_factor": 2000, "imbalance_reconstruction_method": null
      },
      "first_bar_ts": "...", "last_bar_ts": "...", "sidecar": null
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

Field semantics:
- `kind: "aggregated"` → bar loader. `kind: "side"` (incl. `.flow` sidecars) → `CsvFeedSeriesLoader`.
- `build.partitions_written` is authoritative truth for monthly vs. weekly layout per month.
- `sidecar` → companion `FeedSeries` feed-id (auto-bound for the primary, see §7.4); `null` if no sidecar.
- `threshold.input_mode` ∈ `{"absolute", "convenience"}`. Resolved value is canonical.

**Fidelity fields:**
- `estimated_overshoot_pct` = analytic `100 / (2 × n_factor)` where `n_factor = threshold / median_source_record_value`. Computed pre-run; used to warn when N is small.
- `actual_overshoot_pct` = mean over emitted bars of `(realized_threshold − N) / N × 100`.
- `max_overshoot_pct` = worst-case overshoot.
- `imbalance_reconstruction_method` (EqI only): `"tick_signed"` (true Lee-Ready) or `"m1_taker_buy_proxy"` (approximation; UI shows warning).

For `ticks` source, the analytic estimate approaches zero and `actual_overshoot_pct` should be ≤ 0.05 % in practice.

**Atomicity:** writer emits partition files first, then updates `feeds.json` last via write-temp-then-rename. The presence of the `feeds.json` entry is the "feed is complete" marker.

## 6. HistoryLoader REST API

Sync HTTP. Response models normalize naming across exchanges.

### 6.1 Discovery

```
GET /api/v1/exchanges                                              → exchanges + asset counts
GET /api/v1/exchanges/{exchange}/assets                            → assets + feed list
```

Each `feeds[]` entry has:
```json
{ "id": "EqV_1m_1000", "kind": "OHLCV_AltBar", "size": 18421,
  "first_ts": "...", "last_ts": "...", "sidecar": null }
```

`feeds[].kind` ∈ `{ OHLCV_TimeBar, OHLCV_AltBar, Tick, Side }`. `OHLCV_AltBar` may reference a `sidecar` feed-id; the UI groups sidecars (kind `Side`, id ending `.flow`) under their parent feed.

### 6.2 Feed inspection

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status
```
Returns the `feeds.json` entry for aggregated/side feeds; for time bars and ticks returns a partition-scan-derived minimal status (first/last partition month, record count, range).

### 6.3 Aggregation eligibility

```
GET /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options
```
Returns compatible bar types from the §8 matrix, with default thresholds and per-type fidelity hints (incl. EqI proxy warning when source is a time bar).

### 6.4 Aggregation command

```
POST /api/v1/exchanges/{exchange}/assets/{asset}/aggregate
```
Body: `{ source_feed_id, type_code, threshold, threshold_unit, overwrite_existing, from_ts, to_ts }`.

**Default contract: 202 Accepted + SSE progress** (see §6.6 — sync-only POST is not a viable default given reverse-proxy idle limits).

```
202 Accepted, body { "jobId": "..." }, Location: /api/v1/aggregations/{jobId}/progress, X-Job-Id: ...
GET /api/v1/aggregations/{jobId}/progress       → SSE stream, final event = §6.4 summary
GET /api/v1/aggregations/{jobId}                → 200 (complete) | 202 (running)
```

Final summary payload:
```json
{ "feed_id": "EqV_1m_1000", "feeds_json_path": "binance/BTCUSDT_perp/feeds.json",
  "sidecar_feed_id": null,
  "fidelity": { "actual_overshoot_pct": 2.42, "max_overshoot_pct": 18.7, "estimated_overshoot_pct": 2.5 },
  "duration_seconds": 38, "bar_count": 18421, "partitions_written": 80 }
```

**Convenience sync mode:** `Prefer: wait=N` (RFC 7240, N ≤ 3600 s) holds the request open until completion or N-second timeout, then returns 200 with the same summary. Requires Kestrel `KeepAliveTimeout = 60 min` on the HistoryLoader host.

Errors:
- 409 Conflict — feed exists and `overwrite_existing=false`.
- 422 Unprocessable Entity — type incompatible with source.
- 423 Locked — another aggregation targets the same `feed_id` (per-`feed_id` mutex).

### 6.5 Feed deletion

```
DELETE /api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}
```
Allowed only for `OHLCV_AltBar`. Time bars / ticks / side feeds → 403.

### 6.6 Why not sync-only

Sync-only POST forces every aggregation onto a single TCP connection — fragile against reverse-proxy idle defaults (60–120 s typical) and Kestrel `RequestHeadersTimeout` (30 s). 202+SSE works behind any HTTP/1.1 proxy with chunked transfer encoding. The 60-min `KeepAliveTimeout` is retained for `Prefer: wait=N` only.

## 7. Aggregation Pipeline (HistoryLoader internals)

### 7.1 Wall-time envelope

| Source | Scale | Approx. records | Wall time |
|---|---|---|---|
| `1m` | 1 sym × 5 y | 2.6 M | seconds |
| `1m` | 50 sym × 5 y (sequential) | 130 M | minutes |
| `ticks` | BTCUSDT × 1 y | 100 M | minutes |
| `ticks` | BTCUSDT × 5 y | 500 M | tens of minutes |

All comfortably within the 60-min `Prefer: wait=N` envelope. Async/job-queue is a Phase 6 follow-up if very-long tick jobs become routine.

### 7.2 Streaming aggregator

```
PartitionedSourceReader → BarAccumulator → PartitionedSinkWriter → FeedsJsonFinalizer
```

- **Source reader:** enumerates source partitions in chronological order across month boundaries as a single logical stream. When the type needs flow data (EqI from time bars; opt-in EqT/EqV/EqD), simultaneously enumerates the matching `candle-ext` side feed and joins by `ts` 1:1. Spot has no `candle-ext` → EqI rejected at eligibility-check time (§8). Tick sources never join `candle-ext` (per-tick `is_buyer_maker` is intrinsic).
- **Partial-coverage handling:** when `candles/` and `candle-ext/` cover different ranges, aggregator picks the intersection from each feed's first/last timestamps and surfaces the resolved range in the §6.4 summary; bars outside the intersection are not emitted.
- **Memory bounded:** O(1) per accumulator, O(write_buffer_size) per partition.
- **Pipelined:** stages connected via `System.Threading.Channels`.
- **Batched I/O:** 10k-row read chunks; flush every 5k bars per partition.
- **Atomic writes:** `<filename>.tmp` then rename per partition; `feeds.json` rewritten last.
- **Size-budget overflow:** when monthly partition byte count exceeds budget, writer rolls into `<YYYY>-<MM>-W<n>.csv`. Decision is sticky for the month; subsequent months pre-open weekly when prior month overflowed.

```csharp
public interface IBarAccumulator
{
    bool TryAdvance(in SourceRecord r, out AggregatedBar emitted);
    AggregationStats Finalize();
}
```

### 7.3 Per-type accumulators

| Type | State | Emission rule |
|---|---|---|
| EqT | tick counter, OHLC, optional buy/sell split | `counter ≥ N` |
| EqV | base-volume accumulator, OHLC | `base_volume_acc ≥ N` |
| EqD | quote-volume accumulator, OHLC | `quote_volume_acc ≥ N` |
| EqI | signed accumulator (per-tick `is_buyer_maker` from ticks; per-row taker-buy proxy from time bars), OHLC, abs comparator | `abs(signed_acc) ≥ N` |

OHLC: from time-bar source, first-open / max-high / min-low / last-close, volumes summed; buy/sell split joined from `candle-ext` (`taker_buy_vol`). From ticks, derived per-tick.

### 7.4 Overshoot

Per emitted bar: `overshoot_pct_i = (realized_threshold_i − N) / N × 100`. Track running mean and max; persist both alongside the analytic estimate in `feeds.json`.

## 8. Compatibility Matrix (source → eligible types)

In this codebase, taker-buy data lives on the `candle-ext` side feed (`HistoryLoader.Domain/FeedNames.cs:6`), written **only for futures assets** (`BinanceFuturesClient.CandleExtColumns`). Spot ships without it (`BinanceSpotClient.CandleExtColumns => null`).

| Source kind | Asset class | Eligible types | Notes |
|---|---|---|---|
| Tick | any | EqT, EqV, EqD, EqI, future Range/Renko | Highest fidelity. |
| OHLCV_TimeBar **+ candle-ext** | Perp / Future | EqT, EqV, EqD, EqI* | EqI* persists `imbalance_reconstruction_method = "m1_taker_buy_proxy"`; UI surfaces fidelity warning. |
| OHLCV_TimeBar without candle-ext | Spot | EqT, EqV, EqD | EqI not buildable until tick ingestion lands; eligibility endpoint omits EqI. |
| OHLCV_TimeBar OHLC-only (no V) | any | future Range / Renko | Volume-based not buildable. |
| OHLCV_AltBar | any | none | No re-aggregation in v1. |
| Side (funding, OI, candle-ext, …) | any | none | Aggregate button disabled. |

Eligibility check inspects `feeds.json` for `candle-ext` covering the requested interval before listing EqI. Spot/EqI follow-up: when spot tick ingestion (Phase 2a) lands, EqI becomes available via the tick path.

## 9. AlgoTradeForge Main API (proxy layer)

### 9.1 Why mirror, not direct FE→HistoryLoader

Single auth pipeline, single CORS origin, single FE base URL, FE never learns HistoryLoader's host. ~150 LOC of typed `HttpClient` + DTOs is cheaper than YARP for ~6 endpoints; reach for YARP only when hosting multiple HistoryLoader instances.

### 9.2 Mirror contract

All HistoryLoader endpoints (§6) mirrored under `/api/data/...` with identical contracts:
- Forwards via `IHttpClientFactory` + typed `HistoryLoaderClient` (DI, `IOptions<HistoryLoaderOptions>{ BaseUrl, RequestTimeout }`).
- Adds AuthN/AuthZ before forwarding.
- Caches `GET /api/data/exchanges` and `.../assets` (5 s TTL); cache invalidation is **event-driven** — `POST .../aggregate` and `DELETE .../feeds/{id}` clear the affected `(exchange, asset)` key on success.
- 5xx → structured `ProblemDetails` with stable error codes.
- Single `MapDataEndpoints()` extension following the existing minimal-API pattern.

### 9.3 SSE & sync convenience

The proxy mirrors the HistoryLoader 202+SSE contract end-to-end. `Prefer: wait=N` is forwarded; the proxy holds the request open for the same N. SSE shipped from Phase 3 (not deferred).

### 9.4 Frontend invariant

The FE talks **only** to the main `WebApi`, including in development. Single CORS origin, single auth surface, single observability pipeline.

## 10. AlgoTradeForge API: Subscription Redesign

The current `Application.DataSubscriptionDto` (`AssetName, Exchange, TimeFrame`) is replaced by a polymorphic hierarchy at the API/command boundary. The strategy-side `Domain.Strategy.DataSubscription` (which carries an `Asset` object) is a separate concern; sidecar interest is expressed via a typed accessor (§10.4), not through `DataSubscription.FeedKey`. No backward-compat shims; existing callers migrated.

### 10.1 `TimeFrame` value type (Phase 1 prerequisite)

No `TimeFrame` type exists today; timeframe is raw `TimeSpan` with formatting via the static `TimeFrameFormatter`. Phase 1 introduces:

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

Wraps `TimeSpan` so existing `TimeSpan` call sites keep compiling. Phase 4 removes the raw-`TimeSpan` overloads.

### 10.2 Subscription hierarchy

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
{ public override string FeedId => $"{Type.Code}_{SourceFeedId}_{Threshold}";
  public override DataFeedKind Kind => DataFeedKind.AltBar; }

public sealed record TickSubscription(string Exchange, string Asset)
    : DataFeedSubscription(Exchange, Asset)
{ public override string FeedId => "ticks"; public override DataFeedKind Kind => DataFeedKind.Tick; }

public sealed record SideFeedSubscription(string Exchange, string Asset, string SideFeedId)
    : DataFeedSubscription(Exchange, Asset)
{ public override string FeedId => SideFeedId; public override DataFeedKind Kind => DataFeedKind.Side; }
```

### 10.3 Backtest input model

```csharp
public sealed class BacktestInputs
{
    public required DataFeedSubscription Primary { get; init; }   // Role must be Primary
    public IReadOnlyList<DataFeedSubscription> SideFeeds { get; init; } = [];
    public DateRange Range { get; init; }
}
```

Constraints: exactly one Primary; Primary may be `TimeBarSubscription` or `AltBarSubscription`; `Tick` and `Side` are side-only.

Engine resolves a subscription to a CSV glob:

| Kind | Glob |
|---|---|
| TimeBar | `<root>/{Exchange}/{Asset}/candles/*_{FeedId}.csv` |
| AltBar | `<root>/{Exchange}/{Asset}/aggregated/{FeedId}/*.csv` |
| Tick | `<root>/{Exchange}/{Asset}/ticks/*.csv` |
| Side | `<root>/{Exchange}/{Asset}/{FeedId}/*.csv` |

Same `*.csv` glob covers monthly and weekly-overflow files; lex sort = chronological. Bar CSV schema is identical across TimeBar and AltBar (§4.5), so `PartitionedCsvBarLoader` serves both with only path-resolution branching.

### 10.4 `Int64Bar` shape & flow access

Alt bars share the existing `Int64Bar` (6 longs, 48 B):

```csharp
public readonly record struct Int64Bar(
    long TimestampMs, long Open, long High, long Low, long Close, long Volume);
```

Strategies, indicators, exporters, charts, reporting all consume `Int64Bar` — none need to know how a bar was produced. Universal invariants: `TimestampMs` strictly monotonic; `Low ≤ Open, Close ≤ High`; `Volume ≥ 0` (sign-of-volume direction encoding rejected).

Flow data (imbalance, buy/sell split, overshoot) lives in the sidecar `FeedSeries`. Strategies subscribe via a **typed accessor on `IFeedContext`** — not a magic string key — eliminating namespace collision risk:

```csharp
public interface IFeedContext
{
    // Existing — note `out double[]` (NOT ReadOnlySpan) per Domain/Strategy/IFeedContext.cs:16.
    // Returned array is a shared buffer; strategies must NOT hold across bars.
    bool TryGetLatest(string feedKey, out double[] values);
    bool HasNewData(string feedKey);
    DataFeedSchema GetSchema(string feedKey);

    // New (Phase 2b, default-interface methods).
    // Returns false when the primary is not an alt bar OR has no sidecar.
    bool TryGetPrimarySidecar(out double[] values) { values = []; return false; }
    PrimarySidecarSchema? PrimarySidecarSchema => null;
}

public sealed record PrimarySidecarSchema(IReadOnlyList<string> Columns);
```

Usage:
```csharp
public sealed class MyEqIStrategy : IInt64BarStrategy, IFeedContextReceiver
{
    private IFeedContext? _ctx;
    private int _imbCol = -1;
    public void OnFeedContext(IFeedContext ctx)
    {
        _ctx = ctx;
        _imbCol = ctx.PrimarySidecarSchema?.Columns.IndexOf("signed_imbalance") ?? -1;
    }
    public void OnBar(in Int64Bar bar)
    {
        if (_imbCol >= 0 && _ctx!.TryGetPrimarySidecar(out var v))
        {
            var imb = v[_imbCol];
            if (double.IsNaN(imb)) return;   // unavailable for this bar, see §13 Q3
            // ... use imb
        }
    }
}
```

Engine binds `TryGetPrimarySidecar` to the `FeedSeries` named by the primary's `sidecar` field in `feeds.json`. Strategies that don't call it pay zero cost (sidecar lazy-loaded only when at least one consumer's init resolves a non-null schema). Default-interface methods preserve plugin ABI; if Phase 1 DIM audit blocks DIMs on `IFeedContext`, fall back to a separate opt-in `ISidecarReceiver` interface registered alongside `IFeedContextReceiver`.

User-declared side feeds (funding, OI, …) remain reachable via `TryGetLatest(feedKey, …)` as today.

### 10.5 Loader signature change (Phase 1, breaking)

Today: `IInt64BarLoader.Load(dataRoot, exchange, symbol, from, to, TimeSpan interval)`. Alt bars have no fixed interval, so:

```csharp
TimeSeries<Int64Bar> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to);

public readonly record struct DataFeedDescriptor(
    string DataRoot, string Exchange, string Asset, string FeedId, DataFeedKind Kind);
```

`PartitionedCsvBarLoader` gains `Kind`-based path resolution: TimeBar → `candles/<YYYY-MM>_<FeedId>.csv`; AltBar → `aggregated/<FeedId>/<YYYY-MM>.csv` (`*.csv` glob picks up weekly overflow). Doing this in Phase 1 (not Phase 4) avoids a forced refactor of half the Application layer later.

**Migration inventory** (audit before Phase 1):

*API/command boundary (DTO replacement):*
- `src/AlgoTradeForge.Application/DataSubscriptionDto.cs` — replaced by `DataFeedSubscription` hierarchy in commands/responses.
- `src/AlgoTradeForge.Application/Backtests/BacktestPreparer.cs:34` — primary selection is today positional (`command.DataSubscriptions[0]`); replace with `BacktestInputs.Primary`.
- All optimization/validation/debug command handlers under `Application/{Optimization,Validation,Debug}/`.

*Loader & infrastructure:*
- `src/AlgoTradeForge.Application/CandleIngestion/IInt64BarLoader.cs` — interface signature change.
- `src/AlgoTradeForge.Infrastructure/History/PartitionedCsvBarLoader.cs` — impl + glob-based listing.
- `src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvInt64BarLoader.cs` — legacy impl, evaluate for deletion.
- `src/AlgoTradeForge.Infrastructure/Persistence/HistoryRepository.cs`.
- All tests under `tests/AlgoTradeForge.{Application,Infrastructure}.Tests/` referencing the loader.

*Strategy-side (separate concern):*
- `src/AlgoTradeForge.Domain/Strategy/DataSubscription.cs` is **not** replaced by the API hierarchy — it carries a resolved `Asset` object for strategy declaration. Its `FeedKey = "ohlcv"` default has only one literal usage in the solution today (the default itself); decide whether to keep, retire, or replace with a constants class once sidecar-via-typed-accessor lands.
- Private repo: any custom strategy factory that builds subscriptions.

### 10.6 Optimization, validation, debug

- **Backtest / Debug:** single Primary, list of side feeds.
- **Optimization:** `BacktestInputs.Primary` becomes `IReadOnlyList<DataFeedSubscription> PrimaryCandidates`; engine fans out across primaries × parameter grid.
- **Validation (walk-forward / OOS):** same Primary; range split server-side.

## 11. UI

### 11.1 Data Tab

New top-level tab left of "Backtest". Layout per attached sketch.

**Left panel — History tree.** Per-exchange expandable cards. Inside an exchange: data grid with assets as rows and feeds as columns. Columns dynamic, union of feeds across the exchange's assets, stable order:
1. Time bars in canonical order (`1m`, `5m`, `15m`, `1h`, `4h`, `1d`, …).
2. Aggregated bars grouped by type code, sorted by threshold ascending.
3. `ticks` and other source-eligible feeds.
4. Side feeds rightmost, dimmed.

Cell content: `+` if feed exists, `−` otherwise. `+` clickable → opens right sidebar. Aggregated cells with a sidecar render an indicator dot.

**Right sidebar.** Two cards:
- **Status** — read-only Monaco editor showing the active feed's `feeds.json` entry (or partition-derived status for time bars / ticks).
- **New aggregate bar** — form with Source dropdown (eligible feeds for the asset), Type dropdown (filtered by source eligibility per §8), N input (with type-appropriate format hint, accepts SI like `1k`, `10M`), Aggregate button (disabled until valid).

Click Aggregate → `POST /api/data/.../aggregate`. Whole tab UI locks behind translucent overlay with elapsed-time spinner; on success a new column appears (or `−` flips to `+`); toast confirms with `actual_overshoot_pct`.

**Eligibility surfacing:** Aggregate button inert with tooltip when source is `OHLCV_AltBar` ("re-aggregation not supported in v1") or `Side` ("cannot build bars from this feed"). When source is `OHLCV_TimeBar` and type is `EqI`, render yellow banner: *"Imbalance built from time bars uses the taker-buy proxy and underestimates intra-bar churn. Sign-of-imbalance strategies are unaffected; magnitude-sensitive strategies should rebuild from `ticks` when available."* Button stays enabled (warning is informational). Built feed's Status card shows the same banner so downstream runs inherit the warning.

### 11.2 Backtest / Optimization launch

- Primary feed dropdown populated from `/api/data/.../feeds`. Friendly labels (`EqV/1m:1k`, `1m`, `ticks`); icons distinguish time bars from alt bars.
- Side feeds: existing multi-select, now able to include alt bars.
- Optimization: Primary dropdown becomes a multi-select chip control; UI shows estimated run count (`primaries × param_combos`).

## 12. Phased Rollout

**Phase 0 — Pre-flight audits.**
- DIM audit on `IFeedContext` additions: validate `AssemblyLoadContext.Default` semantics on .NET 10, every `IFeedContext`-implementing class in public + private repos, and the JIT path that resolves DIMs. Fall back to `ISidecarReceiver` if unsafe.
- `IInt64BarLoader` external-consumer audit (private repo, plugins).

**Phase 1 — Storage & HistoryLoader core + loader signature change.**
- Storage convention, `EqT`/`EqV`/`EqD` accumulators, `feeds.json` extension with fidelity, per-`feed_id` aggregation mutex (423 Locked).
- Time-bar source only.
- `IInt64BarLoader` signature change to `DataFeedDescriptor` (§10.5).
- Glob-based partition listing for `PartitionedCsvBarLoader`. **Per-timeframe filter** required: `candles/*_*.csv` matches multiple timeframes; new code must filter by requested `FeedId` before sorting. Add regression tests for mixed-timeframe `candles/` dirs.
- HistoryLoader.WebApi greenfield endpoints: `/api/v1/aggregate` (POST, 202+SSE primary), `/api/v1/aggregations/{jobId}/progress` (GET SSE), `/api/v1/aggregations/{jobId}` (GET), `DELETE /api/v1/.../feeds/{id}`. Per-`feed_id` mutex, `X-Job-Id` header, atomic `feeds.json` writer hook (today only `FeedSchemaManager.EnsureSchema` is the writer; needs a service-layer wrapper).
- `CsvFeedSeriesLoader` NaN-sentinel update for sidecars (§13 Q3).
- Resolve scale alignment for `qty` and threshold (§13 Q1).

**Phase 2a — Tick infrastructure.**
- Tick ingestion service (writes §4.6 daily partitions with `agg_id` resume).
- Tick reader (`IInt64TickReader` or extension to `PartitionedCsvBarLoader`).
- `feeds.json` registration for `ticks` feed kind.
- Aggregator `PartitionedSourceReader` tick-source mode (chronological across daily partitions).
- Validation: re-run `EqV`/`EqD`/`EqT` from tick source on at least BTCUSDT_perp × 1y; confirm `actual_overshoot_pct ≤ 0.05 %` vs. time-bar baseline.
- **No EqI yet.** Splitting from Phase 2b is intentional — tick storage is zero-LOC today and folding it into EqI obscures actual surface area.

**Phase 2b — EqI + sidecar pipeline + `IFeedContext` extension.**
- Signed accumulator, EqI type with `tick_signed` / `m1_taker_buy_proxy` reconstruction-method tagging.
- `.flow` sidecar `FeedSeries` writer + reader (reuses `CsvFeedSeriesLoader` post-Q3 update).
- `IFeedContext.TryGetPrimarySidecar` + `PrimarySidecarSchema` (DIM, falling back to `ISidecarReceiver` per Phase 0 audit).
- UI fidelity warning surface for time-bar-source EqI.

**Phase 3 — Main API proxy + SSE + Data Tab UI.**
- `/api/data/*` mirror endpoints, typed `HistoryLoaderClient`, event-driven cache invalidation.
- SSE progress endpoint (not deferred).
- `Prefer: wait=N` convenience sync mode.
- Data Tab UI with sidecar grouping.

**Phase 4 — Subscription redesign + backtest/optimization launch UI.**
- `DataFeedSubscription` hierarchy and `BacktestInputs.Primary` end-to-end through Application + WebApi.
- Optimization fan-out across `PrimaryCandidates`.

**Phase 5 — Range / Renko accumulators.** Path-dependent; require ticks or sub-minute time bars.

**Phase 6 (if needed) — async/job-queue aggregation.** For very-long tick jobs exceeding 60 min. SSE already in place from Phase 3; this adds durable queuing + resumption.

## 13. Open Items

1. **Scale alignment for quantity & threshold.** §4.4 / §4.6: `qty` long-scaling and EqV/EqI-base threshold N must use the same scale factor (probably the asset's quantity-step factor via `ScaleContext`). Misalignment would silently produce wrong-by-1eK thresholds. Pin in Phase 1; document on `Asset` and apply uniformly in tick CSV writes and accumulator comparisons.

2. **Threshold convenience input UX.** Sketch label "Equal volume bar built of 1k 1m" is ambiguous: absolute 1000 base units vs. ≈1000 1m candles' worth. Decision: store absolute, accept either input mode, surface both in `feeds.json` (`threshold.input_mode` + `threshold.convenience_input`). Confirm sketch intent.

3. **Empty cells in sidecar CSVs (resolved, two-pronged).** Empty string in a sidecar cell decodes to `double.NaN`; bar-file cells remain non-nullable `long`. Today `CsvFeedSeriesLoader` (`Infrastructure/History/CsvFeedSeriesLoader.cs:108-128`) handles two cases: (a) missing column (row has fewer fields than header) → fills `0d`; (b) present-but-unparseable cell (incl. empty `","`) → row skipped. Phase 1 adds an empty-string → `NaN` path **gated on sidecar feeds** (`kind: side`, feed-id ending `.flow`); other unparseable cells (e.g. `"abc"`) still skip the row. Existing time-bar / side-feed reads byte-identical. Strategies must guard sidecar reads with `double.IsNaN(v)`. Regression tests cover: malformed time-bar row still skipped; sidecar empty cell yields `NaN` row-kept; sidecar `"abc"` row still skipped.

4. **Multi-instance HistoryLoader.** v1 assumes single instance via `IOptions<HistoryLoaderOptions>{ BaseUrl }`. YARP is the upgrade path.

5. **Resumable / partial aggregations.** v1 always aggregates the full source range. `from_ts` / `to_ts` reserved in API but treated as full-range. Incremental rebuild (re-run only the last partition when source extends) is a natural extension since partitions are calendar-aligned and atomic per partition.

6. **Plugin ABI for `IFeedContextReceiver`** — confirm the interface is public/stable in Domain so private-repo strategies compile against it unchanged. Validated by Phase 0 DIM audit.
