# Binance Archive Backfill + Lazy Feed Loading — Design

**Date:** 2026-07-07
**Status:** Draft (pending user review)
**Scope:** HistoryLoader local profile. Cloud-profile additions (S3 sync, live warm-up serving) are separate roadmap features and out of scope here; the lazy-loading mechanism designed below is profile-independent and will be reused by the cloud instance as-is.

## Problem

HistoryLoader collects all feeds via live REST/WS against Binance. The audit (2026-07-07 session) established:

- Most feeds are fully reconstructible from `https://data.binance.vision` archives (`data/futures/um/...`, `data/spot/...`): klines (all intervals; covers `candles` + `candle-ext` + `mark-price` via `markPriceKlines`), `metrics` (5-minute OI + all three LS ratios, from 2020-09), `fundingRate` (monthly), `aggTrades` (from 2019), um `bookTicker`, spot `1s` klines.
- The `/futures/data/*` REST endpoints only serve the last 30 days — the archive is the *only* source for deep OI/LS history (current config is capped at `HistoryStart: 2026-03-01` because of this).
- `liquidations` is **not** in the archive (liquidationSnapshot discontinued 2021) — it is the only Binance feed that must be captured live, 24/7, or it is lost.
- Spot `bookTicker` is not archived; um `bookTicker` is.
- REST backfill of aggTrades is prohibitively expensive by weight; archive zips are free and unmetered.

Operationally: only irreplaceable data *needs* continuous collection. Replenishable data can be materialized lazily, on demand, for the exact range a backtest needs.

## Goals

1. Materialize any replenishable feed's history from data.binance.vision instead of REST.
2. Classify feeds by replenishability; collect only irreplaceable feeds eagerly (24/7), load replenishable feeds lazily on demand.
3. Expose an on-demand load API (consumed by the frontend Data tab and a coverage hint in Launch) that works for **any Binance symbol**, not just the ones in `appsettings`.
4. Make new resample sources materializable (deep ticks via aggTrades, spot `1s` candles); the existing aggregation `SourceFeedId` mechanism then consumes them unchanged.

## Non-goals

- S3/remote sync of irreplaceable increments (cloud profile, separate feature).
- Warm-up data serving for live strategies (depends on LiveHost Plan 4+, separate feature).
- Backfilling `liquidations` (impossible) or changing the liquidation stream (reconnect fix already landed separately).
- IB data collection (not implemented yet; the classification model below already accounts for it).
- stooq-imported US-equity sibling directories — untouched; everything here is scoped to `{DataRoot}/binance`.

## Design

### 1. Feed replenishability (venue-scoped classification)

A feed is **replenishable** iff an archive materializer is registered for the tuple `(exchange, feedName, assetType)`. Classification is *derived from the materializer registry*, never duplicated in config.

- Registry is keyed by exchange. Binance registers the materializers below. IB (future) registers none → **every IB feed, including plain M1 candles, is irreplaceable by construction** (IB's historical API depth/pacing is too limited to count as a recovery source).
- Asset-type sensitivity: `book-ticker` is replenishable for um futures (archived) but irreplaceable for spot (not archived). `candle-ext` exists only for futures.
- Binance replenishable set: `candles` (all intervals), `candle-ext`, `mark-price`, `open-interest`, `ls-ratio-global`, `ls-ratio-top-accounts`, `ls-ratio-top-positions`, `taker-volume` (derivable from candle-ext), `funding-rate` (archive lacks `mark_price` column — joined from `markPriceKlines` close), `ticks` (aggTrades), futures `book-ticker`.
- Binance irreplaceable set: `liquidations`, spot `book-ticker`.

**Collection policy:** irreplaceable feeds are always collected eagerly (existing stream services + cron collectors, unchanged). Replenishable feeds default to **lazy** — scheduled/eager collection disabled — with an optional per-feed config override `"Eager": true`. The policy check lives where collection is dispatched (`ScheduledCollectorService` / kline daily service / stream service startup), not inside `SymbolCollector`.

**The stream-vs-scheduled axis is orthogonal to replenishability, and the policy governs both.** Concretely: um `book-ticker` is stream-collected AND replenishable — under the lazy default its stream does **not** start (phase 2); real-time capture for it requires `"Eager": true`, which starts the stream. `BookTickerStreamService` therefore filters its symbol set by effective policy, exactly like the cron collectors: spot symbols (irreplaceable) always stream, futures symbols stream only when eager-overridden. (Today `book-ticker` is not enabled for any configured asset, so this changes nothing operationally — but the rule must be explicit so a replenishable stream feed is never silently kept running or silently killed.) `liquidations` and spot `book-ticker` streams are irreplaceable and always run.

### 2. Archive source (Infrastructure)

**`BinanceArchiveClient`** — downloads `https://data.binance.vision/data/{market}/{period}/{dataset}/...` to a system-temp file (zip needs seek; transient, auto-deleted), verifies the `.CHECKSUM` (SHA-256), extracts, retries transient failures with exponential backoff (hand-rolled — no new NuGet packages). No API key, no weight budget; bounded by its own download-concurrency semaphore.

**Known archive parsing quirks** (materializers MUST handle, tests MUST cover):

- Some datasets carry a header row (`metrics`, `fundingRate`), some don't (klines from older periods); detect dynamically (first line starting with a non-digit → skip), never assume.
- Spot archive timestamps switched from milliseconds to **microseconds on 2025-01-01**; unit is detected per row by magnitude, not by hardcoded date. A materializer assuming ms would corrupt every spot month from 2025 onward.
- `metrics` has known missing days at the source; a missing daily zip inside a month is recorded as a `DataGap` (existing gap mechanism) and the job continues — it does not fail the job and does not block month coverage.

**`IArchiveMaterializer`** family behind a factory (per `oop-first-design`; reference impl `IHistoryFeedResolver`). Each materializer maps one archive dataset to one or more of our feeds, writes partitions via the atomic partition writer above, and maintains `feeds.json` entries through `FeedSchemaManager`:

| Materializer | Archive dataset | Our feeds | Notes |
|---|---|---|---|
| `KlinesMaterializer` | `klines/{interval}`, `markPriceKlines/{interval}` | `candles/{interval}`, `candle-ext`, `mark-price` | one implementation parameterized by dataset; candle-ext columns come straight from kline rows |
| `MetricsMaterializer` | `metrics` (daily-only, 5m rows) | `open-interest`, `ls-ratio-global`, `ls-ratio-top-accounts`, `ls-ratio-top-positions` | one daily file feeds 4 feeds; `long_pct = r/(1+r)`, `short_pct = 1/(1+r)`; downsample 5m → the feed's configured interval by timestamp alignment |
| `FundingRateMaterializer` | `fundingRate` (monthly) + `markPriceKlines` join | `funding-rate` | phase 3, low priority (REST already covers full depth); assumption: `mark_price` ≈ markPriceKlines close at the 8h boundary — not the exact funding-time mark price, accepted approximation for backtests |
| `AggTradesMaterializer` | `aggTrades` (daily+monthly) | `ticks` | phase 3; streaming parse, files are GB-scale. **Disk budget:** BTC-scale symbols run ~1–10 GB/symbol-month compressed, several× that as our CSV — a 5-year 20-symbol request is TB-scale. Guard rail: `/api/loads` validates request size against a configurable months×symbols cap for tick loads and rejects oversized requests with an explicit error; deletion stays manual (partitions are plain files; a Data-tab delete action is a later enhancement) |
| (spot only) `KlinesMaterializer` with `1s` | `klines/1s` | `candles/1s` | phase 3; new resample source, spot-only (um archive floor is 1m) |

**Unit of work = monthly partition, closed months only.** A monthly archive zip maps 1:1 onto our `{YYYY-MM}[_{interval}].csv` partition. **The archive path touches only fully closed months; the current month is owned exclusively by the existing REST/stream tail.** This ownership boundary keeps the REST tail alive on every eager cycle, avoids any writer contention on the one partition that has a live in-process append buffer, and prevents re-downloading daily zips for the current month on every run. Where a closed month's monthly zip is not yet published (start of a new month) or the dataset is daily-only (`metrics`), the month is assembled from daily zips. Partition writes are **atomic whole-file replacement** (temp file + rename) by a dedicated partition writer — NOT through the existing append writers (`CandleCsvWriter`/`FeedCsvWriter`): their `BufferedPartitionWriter` base enforces a ratcheting monotonic watermark that would silently drop re-materialized rows. The materializers replicate those writers' CSV formats byte-compatibly instead.

**Coverage = month granularity, completeness-aware.** No separate coverage index, but file existence alone is NOT sufficient: any month that was ever "current" at write time is partial by construction (assembled from daily zips + REST tail), and legacy eager-collected partitions may be incomplete too. A month counts as **covered** iff:

- *Interval feeds* (candles, OI, LS, mark-price, …): the partition exists AND accounts for the **whole** month, not just its tail — the expected row count for a month is known (`intervals-in-month`), so the check is `actualRows + rowsCoveredByRecordedSourceGaps == expectedRows`. This catches holes at the head and middle (legacy eager collection that started mid-month, interrupted jobs topped up by REST), not only a short tail. Counting recorded source `DataGap`s is what prevents a month with days missing *at the source* (`metrics`) from being re-materialized forever. Line-counting a monthly CSV is cheap (a 1m month is ~43k lines); no new metadata needed.
- *Ticks* (no fixed cadence, sparse month-ends are legitimate): the month was materialized from a **monthly** archive zip → complete by construction, recorded as a `CompleteMonths` entry in the feed's `FeedStatus`; months assembled from daily zips/REST are not complete.
- The most recent existing partition of a feed is always a re-check candidate regardless of the above.

An incomplete month is a candidate for (re-)materialization: the load job re-covers it from the archive (idempotent — the monthly zip supersedes the partial partition atomically). Load requests are rounded out to whole months. This keeps sparse tick history workable (a backtest on June 2021 downloads only 2021-06) without an index, and makes the Launch coverage hint honest after phase 2: stale current-month candles show as uncovered, not silently outdated.

### 3. On-demand load API (Application/WebApi)

- `POST /api/v1/loads` `{exchange, symbol, assetType, feedId, from, to}` → creates a background **load job** (registry + queue modeled on the existing Aggregation jobs infra). The job resolves missing **or incomplete** months in `[from, to]` (per the coverage predicate in §2), materializes whole months from the archive, and lets the existing REST path fill the tail. Runs through `BackfillOrchestrator.TryRunSingleAsync` so the per-symbol running-lock and `MaxBackfillConcurrency` still apply.
- `GET /api/v1/loads/{id}` → job progress (months done/total, current dataset, errors).
- `GET /api/v1/coverage?exchange=&symbol=&assetType=` → materialized months per feed (computed from partition files + feeds.json), consumed by the Data tab and the Launch hint.
- **Any Binance symbol:** for a symbol absent from `appsettings`, an `AssetCollectionConfig` is synthesized on the fly; `DecimalDigits` is resolved once from `exchangeInfo` and persisted into the asset's `feeds.json`. On-demand loading does *not* add the symbol to `appsettings` (no auto-enrollment into eager collection).
- `SymbolCollector.CollectFeedAsync` gains an archive-first branch: for a replenishable feed, cover missing whole months via the materializer, then delegate the tail to the existing per-feed REST collector. Irreplaceable feeds are untouched.

### 4. Frontend

- **Data tab:** coverage matrix per asset/feed (from `/api/coverage`), a load-request form (exchange/symbol/feed/date-range), and job progress display.
- **Launch (scope: candle feeds only):** the frontend deterministically knows the primary requirement — exchange/symbol/timeframe/date-range — so the hint checks coverage for the **candle feeds** of the selected configuration and shows a warning with a «догрузить» button firing the same `/api/loads` request. Auxiliary feeds (OI, LS, funding) are auto-discovered server-side at launch time from *whatever exists on disk* (`BacktestPreparer` → `IFeedContextBuilder.Build()`) — they are optional by design, so their coverage is a Data-tab concern, not a Launch gate. A "required feeds for this config" resolution endpoint is a possible later enhancement, explicitly out of scope. The backtest engine itself never blocks on or talks to HistoryLoader.

### 5. Error handling

- **404 for a month** (symbol didn't exist yet / not yet published): mark that month unavailable-at-source within the job result and continue with the remaining months; persist the discovered earliest-available month (analogous to `BinarySearchStartAsync` for REST) so future requests skip it.
- **Checksum mismatch:** one re-download; then fail the job with an explicit error.
- **Network failures:** retry with backoff inside `BinanceArchiveClient`; a failing job reports the error — the collection circuit breaker is not tripped by archive traffic (different host, different failure domain).
- **Concurrent requests for the same symbol:** existing `BackfillOrchestrator` running-symbol lock returns "already running" (a bare `bool`); the load-job registry maintains the `symbol → active job id` mapping itself, so the API can surface a 409 **with the active job id** rather than an opaque conflict.
- **Long archive jobs vs the per-symbol lock (accepted trade-off, phase 1):** a deep aggTrades job can hold a symbol's running-lock for hours, making the daily eager kline backfill for that symbol skip with "already running" (irreplaceable streams — liquidations, spot book-ticker — bypass the orchestrator and are unaffected). Accepted for phase 1; if it bites in practice, revisit with a per-feed lock granularity. The skipped eager day is itself replenishable, so nothing is lost.

### 6. Testing (TDD, per constitution Principle II)

- Materializer unit tests on CSV fixtures: kline row → Int64 candle row (scale via `ScaleContext`), metrics row → 4 feed rows with pct derivation and interval downsampling.
- Quirk fixtures per dataset: with/without header row; spot timestamps in milliseconds AND microseconds (2025+ format); metrics month with a missing day → `DataGap` recorded, job succeeds.
- `BinanceArchiveClient` against `FakeHttpHandler` with in-memory zip + checksum bytes (happy path, checksum mismatch, 404).
- Coverage computation tests over synthetic partition layouts: partial current month; **partial past month** — missing tail, missing head (legacy eager start mid-month), and hole in the middle → all uncovered via row-count check; month whose missing rows are recorded source `DataGap`s → covered (no re-materialization loop); tick month with `CompleteMonths` marker vs without; most-recent-partition re-check.
- Load-job endpoint tests: job lifecycle, unconfigured-symbol synthesis, 409 on concurrent request.
- Classification tests: `(binance, candles, spot)` → replenishable; `(binance, liquidations, perpetual)` → irreplaceable; `(binance, book-ticker, spot)` → irreplaceable; unknown exchange → irreplaceable.

### 7. Phases

1. **Archive source + API:** classification model, `BinanceArchiveClient`, klines/mark-price/metrics materializers, archive-first branch in `SymbolCollector`, `/api/loads` + `/api/coverage` + job registry. Eager collection stays on (no behavior change for existing feeds).
2. **Lazy by default + UI:** flip replenishable feeds to lazy (with per-feed `Eager` override), Data tab, Launch coverage hint.
3. **Heavy sources + cleanup:** `AggTradesMaterializer` (deep ticks), spot `1s` candles as a resample source, `FundingRateMaterializer`, and switch `taker-volume` to materialization from candle-ext. **The `taker-volume` feed and its feed id survive unchanged** — existing backtest configs keep referencing it; only its *source* changes (live REST collector removed, archive/candle-ext materializer takes over).

Phase order guarantees lazy loading is usable before eager collection is switched off.

## Decisions log

- Integrated source in `SymbolCollector` (not a parallel import pipeline, not an external downloader) — single write path, one coverage model.
- Month-granularity coverage computed from partition files — no separate coverage index.
- Any-symbol on demand, without auto-enrollment into `appsettings`.
- Trigger model: explicit (Data tab + Launch hint); no auto-fetch inside backtest preparation.
- Replenishability is venue-scoped and derived from the materializer registry; IB (future) has no archive source → all IB feeds irreplaceable, including M1 candles (their API depth/limits are too poor to count as recovery).
- Local profile only; cloud instance reuses lazy loading unchanged and adds sync/warm-up later.
- Coverage predicate is completeness-aware over the whole month (expected-row-count + recorded gaps for interval feeds / `CompleteMonths` for ticks), not bare file existence and not tail-only — partial months (current, legacy eager starting mid-month, interrupted jobs) must surface as loadable.
- **Closed-months ownership:** archive materialization is restricted to fully closed months; the current month belongs exclusively to the REST/stream tail. One boundary removes the append-writer watermark race, keeps eager tails fresh, and caps archive traffic.
- `DataGap` ends are PRESENT rows (last-before / first-after the hole) — the repo-wide convention; archive materializers derive gaps from parsed-row jump detection, never synthesize them from calendar boundaries.
- Collection policy governs streams as well as cron: a replenishable stream feed (um book-ticker) runs its stream only under `"Eager": true`; irreplaceable streams always run.
- Launch coverage hint is scoped to candle feeds (deterministically known to the frontend); auxiliary-feed coverage lives in the Data tab.
- Coverage wire contract (decided at phase-2 planning): absent `FeedStatus` ⇒ `first_timestamp`/`last_timestamp` are JSON `null` (not 0); load-job `state` is a lowercase wire string (`queued|running|complete|error`), not the enum int.
- Archive gap recording (phase-2 live-smoke fix): the ARCHIVE path records a `DataGap` for ANY missing slot (`jump > interval`) — archive months have exact fixed slots, so every missing slot is a genuine source hole; the streaming/REST path keeps its jitter-tolerant `GapThresholdMultiplier` (> 2×) convention. Sub-threshold single-slot holes otherwise make a month eternally uncoverable (re-materialized every eager cycle; Launch banner never clears) — observed live on spot BTCUSDT 1h 2020-02.
- Frontend API identity (phase-2 live-smoke fix): the catalog `display_name` ("BTCUSDT-perp") is a UI label and never an API key; coverage/load `symbol` is derived from the catalog directory symbol via `exchangeSymbolOf` (strip `_perp`), inverting `AssetPathConvention.DirectoryName`.
- **Phase 3 — spot `1s` klines DROPPED:** sub-minute is not needed yet and consuming it requires a per-asset-type `SourceInterval` in the resolver (currently a single global) — out of scope. Materializing is cheap; the blocker is downstream consumption. Not tracked unless a concrete need arises.
- **Phase 3 — tick on-disk encoding = scaled `long`** (`MoneyConvert.ToLong(value * 10^DecimalDigits)`) in BOTH the archive materializer and the live `DailyTickCsvWriter`/stream/canonicalization paths. `10^DecimalDigits` is the only runtime scale in HistoryLoader (no separate qty step); `PartitionedSourceReader.ReadTicks` parses price/qty as `long`, so archived closed months and the current-month REST/stream tail must agree or the reader throws. Pre-existing raw-decimal tick partitions must be cleared and re-materialized.
- **Phase 3 — tick + funding coverage = `CompleteMonths` marker**, not the interval row-count predicate: both feeds are interval-less (`Interval == ""`, no fixed slot), materialized whole-month from a monthly archive zip. `FeedNames.UsesMonthlyCompleteness(feed)` (ticks or funding-rate) is the single discriminator; a month is marked complete ONLY when sourced from a monthly zip (assembled-from-dailies is re-checked). `MergeStatus` carries the marker through its rebuild (a sealed-class initializer would otherwise wipe it across months).
- **Phase 3 — tick disk-budget guard:** `LoadOptions.MaxTickMonthsPerRequest` (default 24, validated `> 0`) → 422 `tick_load_too_large`. Ticks-only (funding is small and uncapped); single-symbol requests reduce the "months × symbols" product to months.
- **Phase 3 — classification flip → lazy:** registering the ticks/funding-rate/taker-volume materializers makes them replenishable, so the Phase-2 `CollectionPolicy` default makes them **lazy** and their eager collection stops. Stream feeds (spot aggTrade ticks) read policy ONCE at startup → the `:5210` service MUST be restarted after deploy for the flip to take effect. The live taker-volume REST collector was deleted outright (materializer-only henceforth).

## Follow-ups (outside this feature)

- **DONE (Phase 3):** `docs/service-decomposition-vision.md` §HL@cloud updated — un-backfillable set narrowed to `liquidations` + spot `book-ticker`, and the Cloud-profile ↔ `Eager` linkage (archive ~1 day lag ⇒ warm-up-critical feeds kept eager) recorded.
- Per-asset-type `SourceInterval` in `HistoryFeedResolverFactory` + `CsvDataSource` — prerequisite for ever consuming spot `1s`.
- SSE for load jobs (polling is fine at month granularity); a replenishable-feed *options* endpoint to replace the hand-mirrored FE `ARCHIVE_FEEDS` constant.
