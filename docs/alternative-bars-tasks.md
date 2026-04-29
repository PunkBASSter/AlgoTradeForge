# Alternative Bars — Task Tracker

Decomposition of [`alternative-bars-trd.md`](alternative-bars-trd.md) (Draft v5) into a **strictly ordered** task list. Work top-to-bottom, one task at a time. Each task's prerequisites are above it.

**Legend:** `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` skipped/deferred · `[!]` blocked

Tests are interleaved with the implementation they cover so they can be written as soon as the surface they test exists. IDs are stable — they survive reordering, so reference them in commits and PRs.

---

## Phase 0 — Audits + locked decisions

These are blockers. Every audit produces a written decision in `specs/` or this file. No Phase 1 code lands until all five are checked off.

- [ ] **P0-1** DIM audit on `IFeedContext` — verify `AssemblyLoadContext.Default` semantics on .NET 10, enumerate all impls in public + `../AlgoTradeForge.Private/`, confirm JIT DIM dispatch on plugin assemblies. Output: pass/fail decision. (TRD §11 Phase 0, §9.4)
- [ ] **P0-2** Define `ISidecarReceiver` fallback shape **only if** P0-1 fails. Skip if P0-1 passes. (TRD §9.4)
- [ ] **P0-3** `IInt64BarLoader` external-consumer audit — enumerate every callsite in public + private repos. Output: callsite list pinned in `specs/`. (TRD §11 Phase 0)
- [ ] **P0-4** `TimeFrame` raw-`TimeSpan` overload audit — enumerate every callsite of `IInt64BarStrategy` / loader / subscription APIs taking raw `TimeSpan`. Output: callsite list (bounds Phase 4 removal). (TRD §11 Phase 0, §9.1)
- [ ] **P0-5** Lock threshold-input semantics — `feeds.json` always stores absolute canonical units; request payload carries explicit `input_mode ∈ {absolute, convenience}`; `threshold.convenience_input` preserves original. Output: wire-schema doc in `specs/`. (TRD §11 Phase 0, §12 item 1) — _gates P1a-5._

---

## Phase 1a — Foundation (storage + manifest + loader signature)

**Single PR.** Ordered so each task's prerequisites are above it. Bake one week on `main` before Phase 1b starts.

### Scale alignment

- [ ] **P1a-1** Add `ScaleContext.QuantityScale` (and `PriceScale` if not already present); wire on `Asset`. (TRD §3.4) — _gates P1a-15 assertion._
- [ ] **P1a-2** Test: `ScaleContext.QuantityScale` correctness across `CryptoAsset`, `CryptoPerpetualAsset`, `EquityAsset`, `FutureAsset`.

### Naming grammar

- [ ] **P1a-3** Implement positional parser for `<TypeCode>_<SourceCode>_<Threshold>` and `.flow` suffix. Document the `EqV_1m_500m` ambiguity in code comments at the parser. (TRD §3.3, X-5)
- [ ] **P1a-4** Test: parser round-trip incl. ambiguous fixtures (`EqV_1m_500m`, `EqV_5m_500m`, `EqV_1d_1d`).

### Manifest schema

- [ ] **P1a-5** Extend `feeds.json` schema with `kind`, `type`, `source`, `threshold` (incl. `input_mode` per P0-5), `build`, `fidelity`, `first_bar_ts`, `last_bar_ts`, `sidecar`. Schema only — no aggregator. (TRD §4) — _depends on P0-5._
- [ ] **P1a-6** Enforce `fidelity.imbalance_reconstruction_method` MUST be present (null on non-EqI) at write AND read. (TRD §4 rule)
- [ ] **P1a-7** Test: manifest required-field validation — non-EqI without `imbalance_reconstruction_method` errors at both ends.

### Manifest writer + atomicity

- [ ] **P1a-8** Implement per-`(exchange, asset)` synchronized manifest writer with read-merge-write protocol under exclusive lock. Shared lock for readers; exclusive lock for writers. (TRD §4.1)
- [ ] **P1a-9** Manifest writer raises a "manifest changed" event for downstream cache invalidation. (TRD §5.1, §5.3)
- [ ] **P1a-10** Test: concurrent manifest writers — 100× stress, `[Trait("Category", "Stress")]`, `ManualResetEventSlim` align finalizers per iteration.
- [ ] **P1a-11** Co-locate staging dirs + `*.tmp` files on the same volume as target `aggregated/<feedId>/`. Writer rejects cross-volume staging with a clear error. (TRD §3.2)
- [ ] **P1a-12** Test: cross-volume rename guard. CI-skipped if single-volume.

### Startup sweep

- [ ] **P1a-13** Implement startup sweep: delete `*.tmp` + recursively delete orphan `aggregated/<feedId>/`, `<feedId>.flow/`, AND any `.staging-*/` subdirs whose `feedId` is absent from `feeds.json` or whose staging job is no longer running. WARN-log absolute path for each deletion. (TRD §4.1, review fix #2)
- [ ] **P1a-14** Test: startup sweep — orphan `*.tmp` deleted, real partitions untouched.
- [ ] **P1a-15** Test: startup sweep — orphan feed dir deleted + WARN log captured via Serilog test sink.
- [ ] **P1a-16** Test: startup sweep — orphan `.staging-*/` deleted even when manifest entry exists (review fix #2 path).
- [ ] **P1a-17** Test: startup sweep — manifest-entry-without-dir is preserved (asymmetry test).

### Overwrite path

- [ ] **P1a-18** Implement overwrite: stage to `aggregated/<feedId>/.staging-<jobId>/`, atomic rename of staging → live (deleting old live first), then write `feeds.json`. (TRD §4.1)

### Partition writer (sink-side overflow)

- [ ] **P1a-19** Implement `PartitionedSinkWriter` with part-numbered overflow: soft `aggregator.maxPartitionSizeMB=100` budget; sticky-per-month; mid-month first overflow → atomic-rename `<YYYY>-<MM>.csv` → `.p01.csv`, open `.p02.csv`; subsequent months pre-open as `.p01.csv`. Standalone unit (test-fed in 1a; aggregator-fed in 1b). (TRD §3.2, §6.2)
- [ ] **P1a-20** Test: mid-month overflow rename — `PartitionOverflowTests`.
- [ ] **P1a-21** Test: sticky overflow — no `<YYYY>-<MM>.csv` reappears after rollover.

### Scale-tag assertion (no-op accumulator)

- [ ] **P1a-22** Apply scale-tag assertion at accumulator-entry call sites. Phase 1a's "accumulator" is a no-op stub; assertion still wired to lock the contract. (TRD §3.4)
- [ ] **P1a-23** Test: scale-tag mismatch throws at write-time.

### Side-feed reader

- [ ] **P1a-24** Gate `CsvFeedSeriesLoader` empty-cell→`NaN` parsing on manifest `nullable_columns: true`. (TRD §3.5, §11 Phase 1a)
- [ ] **P1a-25** Test: `nullable_columns` gating — empty cells parse to `NaN` when set, throw when unset.

### Loader signature (breaking)

- [ ] **P1a-26** Introduce `DataFeedDescriptor(DataRoot, Exchange, Asset, FeedId, Kind)`. (TRD §9.5) — _depends on P0-3 audit._
- [ ] **P1a-27** Refactor `IInt64BarLoader.Load` to take `DataFeedDescriptor`. Update every callsite from P0-3's enumeration.
- [ ] **P1a-28** Add `Kind`-based path resolution to `PartitionedCsvBarLoader` (TimeBar, AltBar, Tick, Side). (TRD §9.5, §9.3 glob table)
- [ ] **P1a-29** Glob-based partition listing in `PartitionedCsvBarLoader` with per-FeedId filter for mixed-timeframe `candles/` regression safety.
- [ ] **P1a-30** Test: glob-based reader — mixed `.csv` + `.pNN.csv` chronological order; per-FeedId filter excludes `2026-04_5m.csv` when loading `1m`.
- [ ] **P1a-31** Delete legacy `CsvInt64BarLoader` (`src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvInt64BarLoader.cs`). Confirm zero call sites first.
- [ ] **P1a-32** Test: legacy `CsvInt64BarLoader` removed — solution-wide reflective `Type.GetType(...)` returns null.

### Bake

- [ ] **P1a-BAKE** Merge Phase 1a PR; bake one week on `main`. **No Phase 1b work starts until bake clears.**

---

## Phase 1b — Aggregation (REST + queue + accumulators)

Time-bar source aggregation only. Tick sources are Phase 2a.

### Aggregator core

- [ ] **P1b-1** Define `IBarAccumulator` (`TryAdvance`, `Finalize`), `SourceRecord`, `AggregatedBar`, `AggregationStats`. (TRD §6.2)
- [ ] **P1b-2** Implement `EqV` accumulator — base-vol long acc + OHLC. (TRD §6.3)
- [ ] **P1b-3** Test: `EqV` synthetic source, hand-computable boundaries; OHLC = first-open/max-high/min-low/last-close; `vol` = sum(source.vol); `realized_threshold ≥ N`.
- [ ] **P1b-4** Implement `EqT` accumulator — tick counter + OHLC. (TRD §6.3)
- [ ] **P1b-5** Test: `EqT` accumulator parity.
- [ ] **P1b-6** Implement `EqD` accumulator — quote-vol long acc + OHLC. (TRD §6.3)
- [ ] **P1b-7** Test: `EqD` accumulator parity.
- [ ] **P1b-8** Side-feed `double → long` conversion at sum site (`MoneyConvert.ToLong(value * QuantityScale)`); only conversion path. (TRD §3.6, §6.3)
- [ ] **P1b-9** Test: sum-site conversion path — mock `MoneyConvert.ToLong`, verify call count = 1 per source bar.
- [ ] **P1b-10** Implement `PartitionedSourceReader` — chronological enumeration across partition boundaries; optional 1:1 join with `candle-ext` by `ts`. (TRD §6.2)
- [ ] **P1b-11** Wire streaming pipeline: `PartitionedSourceReader → BarAccumulator → PartitionedSinkWriter → FeedsJsonFinalizer` via `System.Threading.Channels`; 10k-row reads, 5k-bar flushes. (TRD §6.2)
- [ ] **P1b-12** Test: streaming memory bound — 1y of 1m source, peak heap O(write-buffer). BDN `[MemoryDiagnoser]` cross-check via `AggregatorBenchmarks` (deferred until P1b-39).
- [ ] **P1b-13** Track per-bar overshoot; persist running mean + max in manifest `fidelity` block. (TRD §6.4)
- [ ] **P1b-14** Test: overshoot stats — synthetic source with median record `0.5N`; assert `actual_overshoot_pct ≈ 100/(2 × n_factor)` within tolerance; max tracked.

### Job infrastructure

- [ ] **P1b-15** Inject `TimeProvider` clock seam on the registry so retention/dedup tests don't wait wall-clock minutes. (review fix #4)
- [ ] **P1b-16** `IAggregationJobRegistry` — `ConcurrentDictionary` dual-keyed by `jobId` and outcome `feed_id`; holds state, timestamps, progress, `Channel<ProgressEvent>` per job; terminal-state retained 15 min via P1b-15 clock. (TRD §6.5)
- [ ] **P1b-17** `IAggregationJobQueue` — bounded `Channel<AggregationJob>` with `aggregator.maxQueueDepth=64`. (TRD §6.5)
- [ ] **P1b-18** `AggregationWorkerHost` — `BackgroundService` running `aggregator.maxConcurrentJobs=2` workers; each runs the §6.2 pipeline serially. (TRD §6.5)
- [ ] **P1b-19** Bind config: `aggregator.maxConcurrentJobs`, `maxQueueDepth`, `jobRetentionMinutes`, `maxPartitionSizeMB`. (TRD §6.5)

### Discovery endpoints

- [ ] **P1b-20** `GET /api/v1/exchanges` — exchanges + asset counts. (TRD §5.1)
- [ ] **P1b-21** `GET /api/v1/exchanges/{exchange}/assets` — assets + feed list per exchange. (TRD §5.1)
- [ ] **P1b-22** `GET /api/v1/assets` — full catalog. (TRD §5.1)
- [ ] **P1b-23** Catalog cache + event-driven invalidation on `feeds.json` change (consumes P1a-9 event); 30 s TTL fallback. (TRD §5.1)
- [ ] **P1b-24** Test: catalog payload shape pinned (FE contract test).
- [ ] **P1b-25** `GET /api/v1/.../feeds/{feedId}/status` — manifest entry verbatim or partition-derived. (TRD §5.2)
- [ ] **P1b-26** `GET /api/v1/.../feeds/{feedId}/aggregation-options` — eligibility payload (eligible types, threshold bounds, warnings). (TRD §5.3)
- [ ] **P1b-27** Eligibility cache shares the catalog invalidation event. (TRD §5.3)

### Aggregate command + SSE

- [ ] **P1b-28** `POST /api/v1/.../aggregate` — async-only, 202 + `Location` + `X-Job-Id`; body schema per §5.4. Enforces status-code precedence `422 → 423 → 409`. Body for `423` carries `{ code, feed_id, existing_job_id, existing_job_state }`. (TRD §5.4)
- [ ] **P1b-29** Per-outcome `feed_id` 423 dedup: block only when existing entry in `queued`/`running`; terminal entries evict on fresh enqueue. (TRD §6.5)
- [ ] **P1b-30** Test: 423 dedup three paths — (a) running ⇒ 423, (b) terminal-within-retention ⇒ 202 (eviction), (c) terminal-after-retention ⇒ 202 (clean). Use P1b-15 clock seam.
- [ ] **P1b-31** `GET /api/v1/aggregations/{jobId}/progress` — SSE stream `queued? → started → progress* → (complete | error)`. (TRD §5.4)
- [ ] **P1b-32** `GET /api/v1/aggregations/{jobId}` — snapshot endpoint. `summary` shape ≡ SSE `complete` payload (minus `type`/`job_id`). (TRD §5.4)
- [ ] **P1b-33** Test: POST 202 + SSE happy path; `complete` payload ≡ snapshot `summary` exactly; `state=running` snapshot has no `summary` field.
- [ ] **P1b-34** SSE `Last-Event-ID` resume; synthetic `started + progress` snapshot when absent; 15-min terminal retention; `410 Gone` thereafter. (TRD §5.4)
- [ ] **P1b-35** Test: SSE reconnect with `Last-Event-ID` (next event); without it (synthetic snapshot first); after retention (410 Gone via P1b-15 clock).
- [ ] **P1b-36** Test: worker pool concurrency — submit 4 distinct-feedId jobs with `maxConcurrentJobs=2`; exactly 2 run; `queue_position` observable.
- [ ] **P1b-37** Test: atomic finalize — kill worker after partition writes but before manifest write → restart sweep cleans staging, catalog reflects "feed missing"; kill after manifest write → feed visible & complete.
- [ ] **P1b-38** Test: threshold input modes — `absolute` and `convenience` round-trip through `feeds.json`.

### Delete endpoint (cascade)

- [ ] **P1b-39** Extend manifest writer to support multi-entry rewrite (parent + sidecar atomic delete). (review fix #5)
- [ ] **P1b-40** `DELETE /api/v1/.../feeds/{feedId}` — `OHLCV_AltBar` only; cascade-delete sidecar dir + both manifest entries in same transactional rewrite; time bars / ticks / side feeds → 403. (TRD §5.5)
- [ ] **P1b-41** Test: cascade-delete — sidecar dir + manifest entry + parent entry all removed atomically.

### Benchmarks (Phase 1b merge gate)

- [ ] **P1b-42** `AggregatorBenchmarks` BDN harness mirroring `BacktestBenchmarks`. Scenarios: `EqV_1m_1000`, `EqT_1m_500` over BTCUSDT 5y. Mean + Allocated. (TRD §11 Phase 1b)
- [ ] **P1b-43** Wire `BriefJsonConfig` on `AggregatorBenchmarks` for `save-baseline.ps1` / `compare-baseline.ps1` ingestion.
- [ ] **P1b-44** Define merge-gate threshold in `scripts/perf/` (e.g. >10% Mean OR any Allocated growth).

---

## Phase 2a — Tick infrastructure

No EqI yet; tick storage and signed accumulator are independent surfaces.

- [ ] **P2a-1** Tick ingestion — daily partitions `History/.../ticks/<YYYY>-<MM>-<DD>.csv` with schema `ts, price, qty, is_buyer_maker, agg_id`. (TRD §3.5)
- [ ] **P2a-2** `agg_id` resume-on-crash — last-written Binance trade ID per day-partition; de-dup on reconnect. (TRD §3.5)
- [ ] **P2a-3** Test: `agg_id` resume — crash mid-day-partition, no duplicate, no skip at boundary.
- [ ] **P2a-4** Tick reader — chronological across daily partitions. (TRD §6.2)
- [ ] **P2a-5** Register `ticks` feed in `feeds.json` per asset. (TRD §3.5, §4)
- [ ] **P2a-6** Aggregator tick-source mode — per-tick OHLC; strict-monotonic `bar.ts_open = max(prev_bar.ts_open + 1, raw_ts)`; +1 ms bump tracked in stats. (TRD §6.3)
- [ ] **P2a-7** Test: tick monotonicity bump — 50 ticks at identical ms; cluster-internal `+1 ms` strictly increasing; stats record N bumps.
- [ ] **P2a-8** Test: tick-source EqT/EqV/EqD parity — re-aggregate to 1m baseline on BTCUSDT_perp slice; assert `actual_overshoot_pct ≤ 0.05%`.
- [ ] **P2a-9** Add tick-source benchmark scenario to `AggregatorBenchmarks`. (TRD §6.5)
- [ ] **P2a-10** Add `aggregator.maxConcurrentTickJobs=1` config gate (separate from time-bar `maxConcurrentJobs`); re-tune per benchmarks. (TRD §6.5)

---

## Phase 2b — EqI + sidecar + `IFeedContext` extension

- [ ] **P2b-1** Implement signed accumulator — tick path (`is_buyer_maker`) and time-bar proxy path (`taker_buy`); emit at `abs(signed_acc) ≥ N`. (TRD §6.3)
- [ ] **P2b-2** EqI eligibility checks — ticks always eligible; time-bar EqI requires `candle-ext` (perp/future only). (TRD §7)
- [ ] **P2b-3** Tag `fidelity.imbalance_reconstruction_method ∈ {tick_signed, m1_taker_buy_proxy}`. (TRD §4)
- [ ] **P2b-4** `.flow` sidecar writer — schema `ts, signed_imbalance, buy_volume, sell_volume, realized_threshold` (all double, `nullable_columns: true`). (TRD §3.5)
- [ ] **P2b-5** `.flow` sidecar reader via `CsvFeedSeriesLoader`. (TRD §3.5)
- [ ] **P2b-6** Sidecar manifest entry registered alongside parent EqI entry; `sidecar` field on parent points to it. (TRD §4)
- [ ] **P2b-7** Test: EqI tick-signed — known buy/sell mix, `signed_acc += +qty` on `is_buyer_maker=0`, `−qty` on `=1`; bar emits at `abs(signed_acc) ≥ N`; sign convention pinned with 100%-buy fixture.
- [ ] **P2b-8** Test: EqI taker-buy proxy — `signed_imbalance = 2 × taker_buy − vol`; manifest tag asserted; sign convention pinned with 100%-taker-buy fixture (review fix #8).
- [ ] **P2b-9** `IFeedContext.TryGetPrimarySidecar` + `PrimarySidecarSchema` (DIM, fallback to P0-2 `ISidecarReceiver` if needed). (TRD §9.4)
- [ ] **P2b-10** Engine binds `TryGetPrimarySidecar` to FeedSeries named by primary's `sidecar` field (lazy load). (TRD §9.4)
- [ ] **P2b-11** Test: sidecar zero-cost — strategy that doesn't call `TryGetPrimarySidecar` triggers zero loader hits.
- [ ] **P2b-12** Test: sidecar binding correctness — mismatched/missing sidecar errors at engine init, not silent NaN at runtime.
- [ ] **P2b-13** UI yellow-banner warning copy for time-bar EqI (consumed by Phase 3 Data tab + Status card). (TRD §10.1)

---

## Phase 3 — Main API proxy + Data Tab UI

### Main API proxy (backend first)

- [ ] **P3-1** Typed `HistoryLoaderClient` via `IHttpClientFactory` + `IOptions<HistoryLoaderOptions>{ BaseUrl, RequestTimeout }`. (TRD §8)
- [ ] **P3-2** AuthN/AuthZ before forwarding. (TRD §8)
- [ ] **P3-3** Single `MapDataEndpoints()` extension mirroring §5 endpoints under `/api/data/*`. (TRD §8)
- [ ] **P3-4** Cache `exchanges`, `exchanges/{e}/assets`, `assets` (5 s TTL). Event-driven invalidation on aggregate/delete. (TRD §8)
- [ ] **P3-5** SSE pass-through with `IHttpResponseBodyFeature.DisableBuffering()` on the proxy route. (TRD §8)
- [ ] **P3-6** 5xx → `ProblemDetails` with stable error codes. (TRD §8)
- [ ] **P3-7** Test: SSE pass-through preserves chunked transfer-encoding; `DisableBuffering()` invoked.
- [ ] **P3-8** Test: cache invalidation — `POST aggregate` clears affected `(exchange, asset)` and catalog keys; concurrent reader sees fresh data.
- [ ] **P3-9** Test: catalog payload shape round-trips through main API unchanged.

### Data Tab UI (frontend after backend stable)

- [ ] **P3-10** Decide horizontal-virtualization library (TanStack Virtual / react-window / custom). Output: ADR. (review Q-1)
- [ ] **P3-11** New top-level "Data" tab (left of "Backtest"). Per-exchange expandable cards. (TRD §10.1)
- [ ] **P3-12** Asset×feed grid; columns dynamic (union of feeds across visible assets); order: time bars → aggregated (by type/threshold asc) → ticks → side feeds. Display names use §3.3 grammar (lowercase `1m`).
- [ ] **P3-13** Horizontal virtualization per P3-10 choice. ≥10k cells regression test.
- [ ] **P3-14** Cell affordance — `+` / `−`; sidecar-bearing aggregated cells render an indicator dot.
- [ ] **P3-15** Right sidebar — Status card (Monaco viewer for `feeds.json` entry).
- [ ] **P3-16** Right sidebar — New aggregate bar card (Source / Type / N / Aggregate). N input accepts SI suffixes; Type filtered by eligibility.
- [ ] **P3-17** SSE progress UI — `Queued (#N)` → `Aggregating <YYYY>-<MM> … X%`. Success: column appears, toast with `actual_overshoot_pct`. Failure: `ProblemDetails` rendered.
- [ ] **P3-18** `localStorage` persistence of `jobId` keyed by `(exchange, asset, feedId)` + `Last-Event-ID` resume.
- [ ] **P3-19** Time-bar EqI yellow banner on the form AND on the built feed's Status card (uses P2b-13 copy).

---

## Phase 4 — Subscription redesign + run-launch UI

### Type-system foundations

- [ ] **P4-1** `TimeFrame` value type — `record struct TimeFrame(TimeSpan)` with `Code`/`Parse`. (TRD §9.1)
- [ ] **P4-2** Migrate every callsite from P0-4 enumeration off raw-`TimeSpan` overloads.
- [ ] **P4-3** Remove raw-`TimeSpan` overloads. (TRD §9.1)
- [ ] **P4-4** Define `DataFeedSubscription` abstract record + `DataFeedKind` + `DataFeedRole` enums. (TRD §9.2)
- [ ] **P4-5** Implement `TimeBarSubscription`, `AltBarSubscription`, `TickSubscription`, `SideFeedSubscription`. (TRD §9.2)
- [ ] **P4-6** Wire polymorphic JSON discriminator via `System.Text.Json`. (TRD §9.2)
- [ ] **P4-7** Test: polymorphic deserialization round-trip across all four subtypes.
- [ ] **P4-8** Test: `AltBarSubscription.FeedId` matches §3.3 grammar; collision-detection asserts component uniqueness.

### `IFeedContext` migration

- [ ] **P4-9** Migrate `IFeedContext` to span-returning `TryGetLatest`, `HasNewData`, `GetSchema`. Update every impl in public + private repos. (TRD §9.4)

### Engine + command boundary

- [ ] **P4-10** Replace `Application.DataSubscriptionDto` with `DataFeedSubscription` at API/command boundary. No back-compat shims. (TRD §1, §9)
- [ ] **P4-11** `BacktestInputs` carries `Primary` (Role=Primary, Kind ∈ {TimeBar, AltBar}) + `SideFeeds`. (TRD §9.3)
- [ ] **P4-12** Engine glob resolution per `Kind` (TimeBar / AltBar / Tick / Side-sidecar / Side-top-level). (TRD §9.3)
- [ ] **P4-13** Test: engine glob resolution — sidecar (`Side` + `<feedId>.flow`) routes nested; top-level side feeds (`funding-rate`) route to asset-root glob.
- [ ] **P4-14** Optimization: `BacktestInputs.PrimaryCandidates` → fan-out across primaries × parameter grid. `IParameterNormalizer` dedup applies per-primary. (TRD §9.6)
- [ ] **P4-15** Test: optimization fan-out — `|primaries| × |combos|` runs; per-primary normalizer dedup intact.
- [ ] **P4-16** Validation — same `Primary`, range split server-side. (TRD §9.6)

### Run-launch UI

- [ ] **P4-17** Backtest/Optimization launch — Primary dropdown sourced from `/api/data/.../feeds`; friendly labels (`EqV/1m:1k`); icons distinguish time vs alt. (TRD §10.2)
- [ ] **P4-18** Side-feed multi-select accepts alt bars. (TRD §10.2)
- [ ] **P4-19** Optimization Primary becomes multi-select chip; estimated run count = `primaries × param_combos`. (TRD §10.2)

---

## Phase 5 — Range / Renko accumulators

Path-dependent; require ticks or sub-minute time bars.

- [ ] **P5-1** Range bar accumulator (configurable price range per bar).
- [ ] **P5-2** Test: Range emission on synthetic price path.
- [ ] **P5-3** Renko bar accumulator (brick size).
- [ ] **P5-4** Test: Renko emission on synthetic price path.
- [ ] **P5-5** Eligibility entries in `aggregation-options` for `Range`/`Renko`. (TRD §7)
- [ ] **P5-6** Decide whether realized-range belongs in `.flow` sidecar or a new sidecar kind. Output: ADR.

---

## Phase 6 — Durable job queue (if needed)

- [ ] **P6-1** Durable queue store (likely SQLite via existing `SqliteRunRepository` infrastructure).
- [ ] **P6-2** SSE replay from event log on reconnect after restart.
- [ ] **P6-3** `DELETE /api/v1/aggregations/{jobId}` — cancel an in-flight job. (TRD §5.4)
- [ ] **P6-4** `from_ts` / `to_ts` partial / resumable aggregation in request schema. (TRD §12 item 3)
- [ ] **P6-5** Re-aggregation from alt-bar sources (e.g., `EqV_2000` from `EqV_1000`); requires variable-bar-duration source reader + fidelity-equivalence proof. (TRD §12 item 5)
- [ ] **P6-6** Replace v1 "interrupted" UX label gap with real interrupted-state delivery from durable registry. (review issue #3)

---

## Cross-cutting (run alongside whichever phase ships them)

- [ ] **X-1** Solution-wide reflective test: no `decimal` in storage layer (walks every type implementing read/write CSV interfaces). Land in Phase 1a. (TRD §11A Cross-cutting, §3.4)
- [ ] **X-2** Confirm `HistoryLoader.Application/Aggregation/**` is in scope of the existing CLAUDE.md "Int64 Money Convention" `(long)` cast rule. Don't add a parallel rule. Land in Phase 1b. (TRD §11A Cross-cutting)
- [ ] **X-3** Existing HistoryLoader registrations (`candle-ext`, `funding-rate`, etc.) flow through the P1a-8 synchronized writer. Audit + migrate. Land in Phase 1a. (TRD §5.3)
- [ ] **X-4** Logging audit: every sweep deletion at WARN with absolute path; every job lifecycle event structured-logged via Serilog. Land in Phase 1b.
- [ ] **X-5** Code comment at the §3.3 positional parser warning about `EqV_1m_500m` ambiguity (parser is positional). Land in Phase 1a (P1a-3).

---

## Open questions

Resolve before the listed gating task. Promote to a `## Resolved` section once locked.

- [ ] **Q-1** Horizontal virtualization library — gates **P3-10**.
- [ ] **Q-2** "Interrupted" vs "failed" job UX in v1 — keep label-only or drop until P6-6? Gates Phase 3 UI copy. (TRD §5.4)
- [ ] **Q-3** Minimum-threshold floor — `1u` canonical vs `max(1u, 1 tick)` to avoid scaled underflow on small-tick assets. Gates P1b-26 eligibility logic. (TRD §3.4)
- [ ] **Q-4** Glob double-load risk — should the reader fail loudly when both `<YYYY-MM>.csv` and `<YYYY-MM>.p*.csv` exist for the same month? Gates P1a-29. (TRD §3.2)

---

## Status summary

| Phase | Tasks | Done | Gating |
|---|---|---|---|
| 0 | 5 | 0 | All before any 1a code |
| 1a | 32 + BAKE | 0 | One PR; one-week bake before 1b |
| 1b | 44 | 0 | Bench gate (P1b-44) before merge |
| 2a | 10 | 0 | — |
| 2b | 13 | 0 | P0-1/P0-2 decision (DIM vs receiver) |
| 3 | 19 | 0 | P3-10 (Q-1 resolved) |
| 4 | 19 | 0 | P0-3 / P0-4 audits drive P4-2 / P4-9 |
| 5 | 6 | 0 | — |
| 6 | 6 | 0 | If needed |
| X | 5 | 0 | Cross-cutting; land with parent phase |
| Q | 4 | — | Each gates a specific task above |
