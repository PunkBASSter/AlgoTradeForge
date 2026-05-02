# Alternative Bars — Task Tracker

Decomposition of [`alternative-bars-trd.md`](alternative-bars-trd.md) (Draft v5) into a **strictly ordered** task list. Work top-to-bottom, one task at a time. Each task's prerequisites are above it.

**Legend:** `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` skipped/deferred · `[!]` blocked

Tests are interleaved with the implementation they cover so they can be written as soon as the surface they test exists. IDs are stable — they survive reordering, so reference them in commits and PRs.

---

## Phase 0 — Audits + locked decisions

These are blockers. Every audit produces a written decision in `specs/` or this file. No Phase 1 code lands until all five are checked off.

- [x] **P0-1** DIM audit on `IFeedContext` — verify `AssemblyLoadContext.Default` semantics on .NET 10, enumerate all impls in public + `../AlgoTradeForge.Private/`, confirm JIT DIM dispatch on plugin assemblies. Output: pass/fail decision. (TRD §11 Phase 0, §9.4) → `specs/031-alternative-bars/p0-1-dim-audit.md` (PASS)
- [-] **P0-2** Define `ISidecarReceiver` fallback shape **only if** P0-1 fails. Skip if P0-1 passes. (TRD §9.4) — _skipped (P0-1 passed)._
- [x] **P0-3** `IInt64BarLoader` external-consumer audit — enumerate every callsite in public + private repos. Output: callsite list pinned in `specs/`. (TRD §11 Phase 0) → `specs/031-alternative-bars/p0-3-int64barloader-callsites.md` (24 callsites)
- [x] **P0-4** `TimeFrame` raw-`TimeSpan` overload audit — enumerate every callsite of `IInt64BarStrategy` / loader / subscription APIs taking raw `TimeSpan`. Output: callsite list (bounds Phase 4 removal). (TRD §11 Phase 0, §9.1) → `specs/031-alternative-bars/p0-4-timespan-callsites.md`
- [x] **P0-5** Lock threshold-input semantics — `feeds.json` always stores absolute canonical units; request payload carries explicit `input_mode ∈ {absolute, convenience}`; `threshold.convenience_input` preserves original. Output: wire-schema doc in `specs/`. (TRD §11 Phase 0, §12 item 1) — _gates P1a-5._ → `specs/031-alternative-bars/p0-5-threshold-wire-schema.md`

---

## Phase 1a — Foundation (storage + manifest + loader signature)

**Single PR.** Ordered so each task's prerequisites are above it. Bake one week on `main` before Phase 1b starts.

### Scale alignment

- [x] **P1a-1** Add `ScaleContext.QuantityScale` (and `PriceScale` if not already present); wire on `Asset`. (TRD §3.4) — _gates P1a-15 assertion._
- [x] **P1a-2** Test: `ScaleContext.QuantityScale` correctness across `CryptoAsset`, `CryptoPerpetualAsset`, `EquityAsset`, `FutureAsset`.

### Naming grammar

- [x] **P1a-3** Implement positional parser for `<TypeCode>_<SourceCode>_<Threshold>` and `.flow` suffix. Document the `EqV_1m_500m` ambiguity in code comments at the parser. (TRD §3.3, X-5)
- [x] **P1a-4** Test: parser round-trip incl. ambiguous fixtures (`EqV_1m_500m`, `EqV_5m_500m`, `EqV_1d_1d`).

### Manifest schema

- [x] **P1a-5** Extend `feeds.json` schema with `kind`, `type`, `source`, `threshold` (incl. `input_mode` per P0-5), `build`, `fidelity`, `first_bar_ts`, `last_bar_ts`, `sidecar`. Schema only — no aggregator. (TRD §4) — _depends on P0-5._
- [x] **P1a-6** Enforce `fidelity.imbalance_reconstruction_method` MUST be present (null on non-EqI) at write AND read. (TRD §4 rule)
- [x] **P1a-7** Test: manifest required-field validation — non-EqI without `imbalance_reconstruction_method` errors at both ends.

### Manifest writer + atomicity

- [x] **P1a-8** Implement per-`(exchange, asset)` synchronized manifest writer with read-merge-write protocol under exclusive lock. Shared lock for readers; exclusive lock for writers. (TRD §4.1)
- [x] **P1a-9** Manifest writer raises a "manifest changed" event for downstream cache invalidation. (TRD §5.1, §5.3)
- [x] **P1a-10** Test: concurrent manifest writers — 100× stress, `[Trait("Category", "Stress")]`, `ManualResetEventSlim` align finalizers per iteration.
- [x] **P1a-11** Co-locate staging dirs + `*.tmp` files on the same volume as target `aggregated/<feedId>/`. Writer rejects cross-volume staging with a clear error. (TRD §3.2) — implemented as `SameVolumeGuard` + integrated in `OverwritePathWriter` and `PartitionedSinkWriter`.
- [x] **P1a-12** Test: cross-volume rename guard. — `CrossVolumeGuardTests` uses an injected `VolumeResolver` to simulate cross-volume layout on single-drive CI hosts (no `[Skip]` needed).

### Startup sweep

- [x] **P1a-13** Implement startup sweep: delete `*.tmp` + recursively delete orphan `aggregated/<feedId>/`, `<feedId>.flow/`, AND any `.staging-*/` subdirs whose `feedId` is absent from `feeds.json` or whose staging job is no longer running. WARN-log absolute path for each deletion. (TRD §4.1, review fix #2)
- [x] **P1a-14** Test: startup sweep — orphan `*.tmp` deleted, real partitions untouched.
- [x] **P1a-15** Test: startup sweep — orphan feed dir deleted + WARN log captured via Serilog test sink.
- [x] **P1a-16** Test: startup sweep — orphan `.staging-*/` deleted even when manifest entry exists (review fix #2 path).
- [x] **P1a-17** Test: startup sweep — manifest-entry-without-dir is preserved (asymmetry test).

### Overwrite path

- [x] **P1a-18** Implement overwrite: stage to `aggregated/<feedId>/.staging-<jobId>/`, atomic rename of staging → live (deleting old live first), then write `feeds.json`. (TRD §4.1)

### Partition writer (sink-side overflow)

- [x] **P1a-19** Implement `PartitionedSinkWriter` with part-numbered overflow: soft `aggregator.maxPartitionSizeMB=100` budget; sticky-per-month; mid-month first overflow → atomic-rename `<YYYY>-<MM>.csv` → `.p01.csv`, open `.p02.csv`; subsequent months pre-open as `.p01.csv`. Standalone unit (test-fed in 1a; aggregator-fed in 1b). (TRD §3.2, §6.2)
- [x] **P1a-20** Test: mid-month overflow rename — `PartitionOverflowTests`.
- [x] **P1a-21** Test: sticky overflow — no `<YYYY>-<MM>.csv` reappears after rollover.

### Scale-tag assertion (no-op accumulator)

- [x] **P1a-22** Apply scale-tag assertion at accumulator-entry call sites. Phase 1a's "accumulator" is a no-op stub; assertion still wired to lock the contract. (TRD §3.4)
- [x] **P1a-23** Test: scale-tag mismatch throws at write-time.

### Side-feed reader

- [x] **P1a-24** Gate `CsvFeedSeriesLoader` empty-cell→`NaN` parsing on manifest `nullable_columns: true`. (TRD §3.5, §11 Phase 1a)
- [x] **P1a-25** Test: `nullable_columns` gating — empty cells parse to `NaN` when set, throw when unset.

### Loader signature (breaking)

- [x] **P1a-26** Introduce `DataFeedDescriptor(DataRoot, Exchange, Asset, FeedId, Kind)`. (TRD §9.5) — _depends on P0-3 audit._
- [x] **P1a-27** Refactor `IInt64BarLoader.Load` to take `DataFeedDescriptor`. Update every callsite from P0-3's enumeration.
- [x] **P1a-28** Add `Kind`-based path resolution to `PartitionedCsvBarLoader` (TimeBar, AltBar, Tick, Side). (TRD §9.5, §9.3 glob table)
- [x] **P1a-29** Glob-based partition listing in `PartitionedCsvBarLoader` with per-FeedId filter for mixed-timeframe `candles/` regression safety.
- [x] **P1a-30** Test: glob-based reader — mixed `.csv` + `.pNN.csv` chronological order; per-FeedId filter excludes `2026-04_5m.csv` when loading `1m`.
- [x] **P1a-31** Delete legacy `CsvInt64BarLoader` (`src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvInt64BarLoader.cs`). Confirm zero call sites first.
- [x] **P1a-32** Test: legacy `CsvInt64BarLoader` removed — solution-wide reflective `Type.GetType(...)` returns null.

### Bake

- [ ] **P1a-BAKE** Merge Phase 1a PR; bake one week on `main`. **No Phase 1b work starts until bake clears.**

---

## Phase 1b — Aggregation (REST + queue + accumulators)

Time-bar source aggregation only. Tick sources are Phase 2a.

### Carryover from Phase 1a review (2026-04-30)

- [x] **P1b-0a** Tighten `CsvFeedSeriesLoader` malformed-cell handling: under both `nullable_columns: true` AND `false`, a non-empty malformed cell (e.g. `"abc"` in a numeric column) MUST throw `FormatException` with file/row/column context — not silently skip the row. Update `CsvFeedSeriesLoaderTests.Load_NonNumericValue_SkipsRow` to assert the throw. Phase 1a kept legacy "skip row" behavior for backward compat; Phase 1b makes data corruption loud. (Review remediation #7)
- [x] **P1b-0b** API-boundary path-traversal validation: every `DataFeedDescriptor` synthesized from `POST /aggregate` request input MUST flow through `AltBarFeedId.TryParse` before hitting the loader / sink. (Review security note) — `FeedIdValidator` + `AltBarFeedId.TryParse`. Wired in `AggregationEndpoints.PostAggregate` for path components, source feed-id, and outcome alt-bar feed-id.

### Aggregator core

- [x] **P1b-1** Define `IBarAccumulator` (`TryAdvance`, `Finalize`), `SourceRecord`, `AggregatedBar`, `AggregationStats`. (TRD §6.2) — _interface + DTOs landed in Phase 1a._
- [x] **P1b-2** Implement `EqV` accumulator — base-vol long acc + OHLC. (TRD §6.3) — `EqVAccumulator : AccumulatorBase`.
- [x] **P1b-3** Test: `EqV` synthetic source, hand-computable boundaries; OHLC = first-open/max-high/min-low/last-close; `vol` = sum(source.vol); `realized_threshold ≥ N`.
- [x] **P1b-4** Implement `EqT` accumulator — tick counter + OHLC. (TRD §6.3) — `EqTAccumulator : AccumulatorBase`.
- [x] **P1b-5** Test: `EqT` accumulator parity.
- [x] **P1b-6** Implement `EqD` accumulator — quote-vol long acc + OHLC. (TRD §6.3) — `EqDAccumulator : AccumulatorBase` (close × volume approximation; candle-ext join in Phase 2b).
- [x] **P1b-7** Test: `EqD` accumulator parity.
- [-] **P1b-8** Side-feed `double → long` conversion at sum site (`MoneyConvert.ToLong(value * QuantityScale)`); only conversion path. (TRD §3.6, §6.3) — _no conversion needed in Phase 1b: candle CSVs are already pre-scaled longs and EqD's product is pure long arithmetic. The sum site lands in Phase 2b's EqI accumulator where `taker_buy_quote_vol` arrives as `double` from the side feed (documented on `EqDAccumulator`)._
- [-] **P1b-9** Test: sum-site conversion path — mock `MoneyConvert.ToLong`, verify call count = 1 per source bar. — _deferred with P1b-8._
- [x] **P1b-10** Implement `PartitionedSourceReader` — chronological enumeration across partition boundaries; optional 1:1 join with `candle-ext` by `ts`. (TRD §6.2) — _candle-ext join reserved for Phase 2b's EqI proxy._
- [x] **P1b-11** Wire streaming pipeline: `PartitionedSourceReader → BarAccumulator → PartitionedSinkWriter → FeedsJsonFinalizer` — `AggregationPipeline.Run`. _Channel use moved to progress flow only (per-job `ProgressEvent` channel for SSE); the inner record path is sync since the accumulator is fast and the sink is single-threaded by design (TRD §6.2)._
- [x] **P1b-12** Test: streaming memory bound — 1000 records under 1 MB allocation delta. BDN `[MemoryDiagnoser]` cross-check deferred to P1b-42 via `AggregatorBenchmarks`.
- [x] **P1b-13** Track per-bar overshoot; persist running mean + max in manifest `fidelity` block. (TRD §6.4)
- [x] **P1b-14** Test: overshoot stats — `Run_OvershootStats_MatchAnalyticEstimate` (median=500, threshold=1000 → n_factor=2, estimated 25%, actual 0% on aligned input).

### Job infrastructure

- [x] **P1b-15** Inject `TimeProvider` clock seam on the registry so retention/dedup tests don't wait wall-clock minutes. (review fix #4) — _hand-rolled `TestClock : TimeProvider` per CLAUDE.md "no new NuGet" rule._
- [x] **P1b-16** `IAggregationJobRegistry` — `ConcurrentDictionary` dual-keyed by `jobId` and outcome `feed_id`; per-job `List<JobEvent>` with monotonic seq for SSE replay; terminal-state retained per `JobRetentionMinutes`. (TRD §6.5) — _per-feed_id locks instead of global; live `Channel<ProgressEvent>` deferred to SD5 with the SSE handler._
- [x] **P1b-17** `IAggregationJobQueue` — bounded `Channel<AggregationJob>` with `aggregator.maxQueueDepth=64`; `ChannelReader.Count` used as authoritative depth. (TRD §6.5)
- [x] **P1b-18** `AggregationWorkerHost` — `BackgroundService` running `MaxConcurrentJobs` workers; each runs the §6.2 pipeline serially via `IServiceScopeFactory`. (TRD §6.5) — _DI registration in SD4/SD5; tests are integration-flavor and land with SD5 endpoint flows._
- [x] **P1b-19** Bind + validate config: `aggregator.maxConcurrentJobs`, `maxQueueDepth`, `jobRetentionMinutes`, `maxPartitionSizeMB`, `maxConcurrentTickJobs`. (TRD §6.5)

### Discovery endpoints

- [x] **P1b-20** `GET /api/v1/exchanges` — exchanges + asset counts. (TRD §5.1)
- [x] **P1b-21** `GET /api/v1/exchanges/{exchange}/assets` — assets + feed list per exchange. (TRD §5.1)
- [x] **P1b-22** `GET /api/v1/assets` — full catalog. (TRD §5.1)
- [x] **P1b-23** Catalog cache + event-driven invalidation on `feeds.json` change (consumes P1a-9 event); 30 s TTL fallback. (TRD §5.1) — _version-bump strategy on `ManifestChanged`; key suffix carries the version, old entries TTL-expire._
- [x] **P1b-24** Test: catalog payload shape pinned (FE contract test) — `FeedCatalogTests` covers shape + ordering; full HTTP-level golden file deferred until FE consumer arrives in Phase 3.
- [x] **P1b-25** `GET /api/v1/.../feeds/{feedId}/status` — manifest entry verbatim. (TRD §5.2)
- [x] **P1b-26** `GET /api/v1/.../feeds/{feedId}/aggregation-options` — eligibility payload (eligible types, threshold bounds, warnings). (TRD §5.3) — `EligibilityRules.ForSource` encodes the §7 matrix.
- [x] **P1b-27** Eligibility cache shares the catalog invalidation event. (TRD §5.3) — _eligibility resolves from cached catalog data; same invalidation chain._

### Aggregate command + SSE

- [x] **P1b-28** `POST /api/v1/.../aggregate` — async-only, 202 + `Location` + `X-Job-Id`; body schema per §5.4. Enforces status-code precedence `422 → 423 → 409`. Body for `423` carries `{ code, feed_id, existing_job_id, existing_job_state }`. (TRD §5.4)
- [x] **P1b-29** Per-outcome `feed_id` 423 dedup: block only when existing entry in `queued`/`running`; terminal entries evict on fresh enqueue. (TRD §6.5)
- [x] **P1b-30** Test: 423 dedup three paths — `AggregationJobRegistryTests.TryEnqueue_FeedAlreadyRunning_ReturnsFeedAlreadyLocked`, `TryEnqueue_FeedTerminalWithinRetention_EvictsAndAccepts`, `TryEnqueue_FeedTerminalPastRetention_AcceptsWithCleanRegistry`.
- [x] **P1b-31** `GET /api/v1/aggregations/{jobId}/progress` — SSE stream `queued? → started → progress* → (complete | error)`. (TRD §5.4)
- [x] **P1b-32** `GET /api/v1/aggregations/{jobId}` — snapshot endpoint. `summary` shape ≡ SSE `complete` payload via shared `CompletePayload(result)` helper. (TRD §5.4)
- [-] **P1b-33** Test: POST 202 + SSE happy path; `complete` payload ≡ snapshot `summary` exactly. — _HTTP integration tests deferred (no new NuGet rule); `CompletePayload` helper used by both code paths is structurally equivalent. Add `Microsoft.AspNetCore.Mvc.Testing` + `WebApplicationFactory` fixture in a follow-up to assert at the wire level._
- [x] **P1b-34** SSE `Last-Event-ID` resume; synthetic snapshot when seq beyond log; 15-min terminal retention; `410 Gone` thereafter (when registry's `Get` returns null). (TRD §5.4)
- [-] **P1b-35** Test: SSE reconnect with `Last-Event-ID` (next event); without it (synthetic snapshot first); after retention (410 Gone via P1b-15 clock). — _HTTP-level tests deferred per P1b-33._
- [-] **P1b-36** Test: worker pool concurrency — _deferred (HTTP integration); registry's `queue_position` exposed via snapshot, but the 4-jobs/2-workers behavior needs the worker host running, which is most easily exercised via WebApplicationFactory._
- [-] **P1b-37** Test: atomic finalize — _deferred. The `OverwritePathWriter` + `StartupSweepService` pair is independently tested (P1a). End-to-end mid-pipeline kill/restart needs HTTP integration._
- [x] **P1b-38** Test: threshold input modes — `ThresholdResolverTests.Resolve_BothInputModesRoundTripToSameAbsolute` (absolute and convenience produce same canonical absolute; FeedIdComponent differs by grammar).

### Delete endpoint (cascade)

- [x] **P1b-39** Extend manifest writer to support multi-entry rewrite (parent + sidecar atomic delete). `ISchemaManager.RemoveFeed` + `RemoveFeedAndSidecar` — read-merge-write under one exclusive lock; single `ManifestChanged` event per call.
- [x] **P1b-40** `DELETE /api/v1/.../feeds/{feedId}` — `OHLCV_AltBar` only (403 on other kinds); rename-aside + recursive delete on dirs; atomic manifest rewrite via `RemoveFeedAndSidecar`. (TRD §5.5)
- [x] **P1b-41** Test: cascade-delete — `FeedSchemaManagerCascadeTests` covers parent+sidecar atomic rewrite, single-event semantics, and no-op-on-missing.

### Benchmarks (Phase 1b merge gate)

- [x] **P1b-42** `AggregatorBenchmarks` BDN harness mirroring `BacktestBenchmarks`. Scenarios: `Aggregate_EqV_1h_100k`, `Aggregate_EqT_1h_500` over BTCUSDT 5y / 1h. Mean + Allocated. — _bundled data is 1h not 1m, scenarios named accordingly._
- [x] **P1b-43** Wire `BriefJsonConfig` on `AggregatorBenchmarks` for `save-baseline.ps1` / `compare-baseline.ps1` ingestion.
- [x] **P1b-44** Define merge-gate threshold in `scripts/perf/aggregator-merge-gate.md` (>10% Mean OR any Allocated growth).

---

## Phase 2a — Tick infrastructure

No EqI yet; tick storage and signed accumulator are independent surfaces.

- [x] **P2a-1** Tick ingestion — daily partitions `History/.../ticks/<YYYY>-<MM>-<DD>.csv` with schema `ts, price, qty, is_buyer_maker, agg_id`. (TRD §3.5) — `BinanceFuturesClient.AggregateTrades` partial + `BinanceAggTradeParser` + `DailyTickCsvWriter` + `AggTradeFeedCollector` + `TicksCollectorService` (5 min PeriodicTimer) + DI wiring.
- [x] **P2a-2** `agg_id` resume-on-crash — last-written Binance trade ID per day-partition; de-dup on reconnect. (TRD §3.5) — `DailyTickCsvWriter.ResumeFrom` reads tail with chunked-backwards scan; torn-row repair via `FileStream.SetLength`; `_lastAggIdByDay` cache rejects replays. Collector advances `fromMs = lastTsMs` (inclusive) so multi-tick-per-ms boundary clusters are re-fetched and dedupped by id, not skipped by ts+1.
- [x] **P2a-3** Test: `agg_id` resume — crash mid-day-partition, no duplicate, no skip at boundary. — `DailyTickCsvWriterTests` covers torn-write truncation, replay dedup across the boundary aggId range, and clean cut at UTC midnight (10 tests).
- [x] **P2a-4** Tick reader — chronological across daily partitions. (TRD §6.2) — `PartitionedSourceReader` gains `Kind`-switch in `Read()` delegating to private `ReadTickFile`; daily glob `????-??-??.csv` lex-sorted ≡ chronological by ISO date; ts maps to `SourceRecord(ts, price, price, price, price, qty)`.
- [x] **P2a-5** Register `ticks` feed in `feeds.json` per asset. (TRD §3.5, §4) — `AggTradeFeedCollector.CollectAsync` calls `SchemaManager.EnsureSchema(assetDir, "ticks", "", ["price","qty","is_buyer_maker","agg_id"])`; `appsettings.json` adds `{"Name":"ticks", "Interval":"", "HistoryStart":"2026-04-15"}` to BTCUSDT_perp.
- [ ] **P2a-BAKE** Merge Phase 2a collection PR (P2a-1..5); bake 24–48 h on `main`. Validates: no duplicate `agg_id`, no day-boundary gaps, no SIGKILL data loss, daily files rotate at UTC midnight. **No Phase 2a aggregator work (P2a-6+) starts until bake clears.**
- [x] **P2a-6** Aggregator tick-source mode — per-tick OHLC; strict-monotonic `bar.ts_open = max(prev_bar.ts_open + 1, raw_ts)`; +1 ms bump tracked in stats. (TRD §6.3) — `MonotonicTickSource` decorator wraps source enumeration when `Kind=Tick`; `BumpCount` property surfaces post-iteration; `AggregationStats.MonotonicBumps` (4th positional, default 0) carries the count; `BuildInfo.MonotonicBumps` (nullable) lands in `feeds.json` for tick jobs only.
- [x] **P2a-7** Test: tick monotonicity bump — 50 ticks at identical ms; cluster-internal `+1 ms` strictly increasing; stats record N bumps. — `MonotonicTickSourceTests` covers all-same-ms (49 bumps), mixed cluster `[t,t,t+5,t+5,t+5]→[t,t+1,t+5,t+6,t+7]` (3 bumps), already-strict (0 bumps), out-of-order, empty, and BumpCount-resets-between-runs (7 tests).
- [-] **P2a-8** Test: tick-source EqT/EqV/EqD parity — re-aggregate to 1m baseline on BTCUSDT_perp slice; assert `actual_overshoot_pct ≤ 0.05%`. — _deferred. Parity test needs real BTCUSDT_perp tick data over a 1-day slice (50–200 MB), which only exists post-`P2a-BAKE`. Synthetic-data benchmark scenarios (P2a-9) catch regressions on the code path; the parity test lands once bake produces real ticks._
- [x] **P2a-9** Add tick-source benchmark scenario to `AggregatorBenchmarks`. (TRD §6.5) — `Aggregate_EqV_FromTicks_1h` + `Aggregate_EqT_FromTicks_1h` scenarios. Synthetic generator produces ~150k deterministic ticks (Poisson interarrival, walking price, exponential qty, fixed seed) at `[GlobalSetup]` — no large fixture checked into git.
- [x] **P2a-10** Add `aggregator.maxConcurrentTickJobs=1` config gate (separate from time-bar `maxConcurrentJobs`); re-tune per benchmarks. (TRD §6.5) — `IAggregationTickJobQueue : IAggregationJobQueue` + `AggregationTickJobQueue` impl; `AggregationWorkerHost` spawns two pools sized by `MaxConcurrentJobs` and `MaxConcurrentTickJobs`; `AggregationEndpoints.PostAggregate` routes by `body.SourceFeedId == "ticks"` to the tick queue. Time-bar workers stay unblocked when tick jobs are active.

---

## Phase 2b — EqI + sidecar + `IFeedContext` extension

- [x] **P2b-1** Implement signed accumulator — tick path (`is_buyer_maker`) and time-bar proxy path (`taker_buy`); emit at `abs(signed_acc) ≥ N`. (TRD §6.3) — `EqIAccumulator` (long signed-acc + raw-double buy/sell for sidecar). `SourceRecord` gains `BuyVolumeLong`/`SellVolumeLong`; tick reader populates from `is_buyer_maker`; `CandleExtJoiningSource` decorates time-bar source for the proxy path.
- [x] **P2b-2** EqI eligibility checks — ticks always eligible; time-bar EqI requires `candle-ext` (perp/future only). (TRD §7) — already encoded in `EligibilityRules.ForSource` (pre-existing); P2b ships compatible accumulator.
- [x] **P2b-3** Tag `fidelity.imbalance_reconstruction_method ∈ {tick_signed, m1_taker_buy_proxy}`. (TRD §4) — `AggregationPipeline` selects by `Source.Kind` (Tick → `tick_signed`, TimeBar → `m1_taker_buy_proxy`).
- [x] **P2b-4** `.flow` sidecar writer — schema `ts, signed_imbalance, buy_volume, sell_volume, realized_threshold` (all double, `nullable_columns: true`). (TRD §3.5) — Pipeline-side `PartitionedSinkWriter` opens a sibling staging dir under `aggregated/<feedId>.flow/`.
- [x] **P2b-5** `.flow` sidecar reader via `CsvFeedSeriesLoader`. (TRD §3.5) — `feedName: aggregated/<feedId>.flow`, `nullable_columns: true` propagated from manifest.
- [x] **P2b-6** Sidecar manifest entry registered alongside parent EqI entry; `sidecar` field on parent points to it. (TRD §4) — `ISchemaManager.EnsureAltBarWithSidecar` writes both atomically under one exclusive lock.
- [x] **P2b-7** Test: EqI tick-signed — `EqIAccumulatorTests` (100%-buy positive sidecar, 100%-sell negative, mixed cancellation) + `AggregationPipeline_EqITests.Run_TickEqI_AllBuy_ManifestTaggedTickSigned`.
- [x] **P2b-8** Test: EqI taker-buy proxy — `AggregationPipeline_EqITests.Run_TimeBarEqI_TakerBuyProxy_FormulaMatchesTrd` (100%-taker-buy fixture asserts positive signed and `signed = 2*taker_buy − vol`); manifest tag pinned by `Run_TimeBarEqI_AllTakerBuy_PositiveSignedImbalance_ManifestTagged`.
- [x] **P2b-9** `IFeedContext.TryGetPrimarySidecar` + `PrimarySidecarSchema` (DIM, fallback to P0-2 `ISidecarReceiver` if needed). (TRD §9.4) — default-interface methods on `IFeedContext`; `BacktestFeedContext` overrides for backtest path. `GetPrimarySignedImbalance()` convenience helper lands as default-method too.
- [x] **P2b-10** Engine binds `TryGetPrimarySidecar` to FeedSeries named by primary's `sidecar` field (lazy load). (TRD §9.4) — `BacktestFeedContext.RegisterPrimarySidecarLazy` + `EnsurePrimarySidecarMaterialized`. `IFeedContextBuilder.Build` gains `primaryFeedName` param; `FeedContextBuilder` skips eager `.flow` load and registers lazy when primary's manifest entry has `Sidecar`. Cursor catches up to `_latestAdvanceTs` on first materialization so mid-run access doesn't replay history.
- [x] **P2b-11** Test: sidecar zero-cost — `BacktestFeedContextSidecarTests.TryGetPrimarySidecar_WhenStrategyNeverAccesses_LoaderNotInvoked` + `_FirstAccess_InvokesLoaderExactlyOnce`.
- [x] **P2b-12** Test: sidecar binding correctness — `BacktestFeedContextSidecarTests.TryGetPrimarySidecar_LoaderReturnsNull_ThrowsInvalidOperationException` (loud failure at first access, not silent `NaN`).
- [x] **P2b-13** UI yellow-banner warning copy for time-bar EqI (consumed by Phase 3 Data tab + Status card). (TRD §10.1) — `AltBarWarnings.TimeBarEqIProxy` constant; `EligibilityRules` returns it via the eligibility-options endpoint so the FE relays the canonical wording without composing copy.

---

## Phase 3 — Main API proxy + Data Tab UI

### Main API proxy (backend first)

- [x] **P3-1** Typed `HistoryLoaderClient` via `IHttpClientFactory` + `IOptions<HistoryLoaderOptions>{ BaseUrl, RequestTimeout }`. (TRD §8) — `src/AlgoTradeForge.WebApi/Data/HistoryLoaderClient.cs` + `HistoryLoaderClientExtensions.AddHistoryLoaderClient`. Thin shell — no JSON deserialization (P3-9 byte-identical contract). 8 unit tests.
- [-] **P3-2** AuthN/AuthZ before forwarding. (TRD §8) — _deferred to Phase 4. Neither WebApi nor HistoryLoader has auth today; introducing it pulls a scheme decision (cookie / JWT / Windows) into Phase 3 scope. Re-open when an auth scheme is chosen._
- [x] **P3-3** Single `MapDataEndpoints()` extension mirroring §5 endpoints under `/api/data/*`. (TRD §8) — `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs`. Catalog GETs (cached), per-feed status / aggregation-options / snapshot (passthrough), POST aggregate, DELETE feed, SSE progress.
- [x] **P3-4** Cache `exchanges`, `exchanges/{e}/assets`, `assets` (5 s TTL). Event-driven invalidation on aggregate/delete. (TRD §8) — `DataProxyCache` (write-through invalidation; main API can't subscribe to upstream's in-process `ManifestChanged` across the process boundary, so write-through + 5-s TTL safety net replaces the event subscription).
- [x] **P3-5** SSE pass-through with `IHttpResponseBodyFeature.DisableBuffering()` on the proxy route. (TRD §8) — `DataEndpoints.ProxySse` forwards Last-Event-ID, sets SSE headers, disables Kestrel buffering, copies upstream stream byte-for-byte.
- [x] **P3-6** 5xx → `ProblemDetails` with stable error codes. (TRD §8) — `DataProxyProblem` (codes: `history_loader_unavailable`/502, `upstream_timeout`/504, `upstream_error` passthrough). 4xx forwarded byte-identical (422/423/409 carry domain-meaningful payloads).
- [x] **P3-7** Test: SSE pass-through preserves chunked transfer-encoding; `DisableBuffering()` invoked. — `DataProxyTests.SsePassThrough_PreservesContentType_AndDisablesBuffering` + `_410Gone_ForwardsStatusAndBody`. `BufferingCapture` decorator records the `DisableBuffering()` call.
- [x] **P3-8** Test: cache invalidation — `POST aggregate` clears affected `(exchange, asset)` and catalog keys; concurrent reader sees fresh data. — `DataProxyTests.PostAggregate_InvalidatesCache_ConcurrentReaderSeesFresh` + `DeleteFeed_InvalidatesCache`.
- [x] **P3-9** Test: catalog payload shape round-trips through main API unchanged. — `DataProxyTests.CatalogPayloads_RoundTripUnchanged_GetExchanges` (+ `_GetAssets`, `_GetExchangeAssets`). Byte-identical assertion on raw response bodies; cache stores opaque bytes (no JSON re-serialization).

### Data Tab UI (frontend after backend stable)

- [x] **P3-10** Decide horizontal-virtualization library (TanStack Virtual / react-window / custom). Output: ADR. (review Q-1) → [`docs/adr/2026-05-virtualization-tanstack.md`](adr/2026-05-virtualization-tanstack.md) (TanStack Virtual, both axes)
- [x] **P3-11** New top-level "Data" tab (left of "Backtest"). Per-exchange expandable cards. (TRD §10.1) — `frontend/app/data/page.tsx` + `DataTabRoot` + `ExchangeCard`. NavBar adds an always-visible "Data" link in its own group; "data" added to `reservedPrefixes` so the strategy/mode parser doesn't consume the URL.
- [x] **P3-12** Asset×feed grid; columns dynamic (union of feeds across visible assets); order: time bars → aggregated (by type/threshold asc) → ticks → side feeds. Display names use §3.3 grammar (lowercase `1m`). — `AssetFeedGrid` + `feed-order.ts` (`compareFeed` + `unionFeedColumns`). Server-supplied `kind`/`type_code`/`threshold_value` drive sort; no §3.3 grammar parser ported to TS.
- [x] **P3-13** Horizontal virtualization per P3-10 choice. ≥10k cells regression test. — `@tanstack/react-virtual` v3 with both-axis `useVirtualizer` instances sharing one scroll container. Test `renders_only_visible_cells_for_10k_grid` asserts <500 buttons in DOM for a 500×20 grid (vs 10000 logical cells).
- [x] **P3-14** Cell affordance — `+` / `−`; sidecar-bearing aggregated cells render an indicator dot. — `FeedCell` component; `+` for absent feeds (clickable, opens new-aggregate form), `−` for present alt-bars (opens Status card), `aria-label="has sidecar"` dot for cells whose feed has a non-null sidecar.
- [x] **P3-15** Right sidebar — Status card (CodeMirror viewer for `feeds.json` entry). — `FeedStatusCard` + `DataSidebar` host (uses existing `SlideOver` primitive). CodeMirror 6 in `EditorState.readOnly.of(true)` + `EditorView.editable.of(false)` mode (TanStack Virtual ADR records the CodeMirror-vs-Monaco choice).
- [x] **P3-16** Right sidebar — New aggregate bar card (Source / Type / N / Aggregate). N input accepts SI suffixes; Type filtered by eligibility. — `NewAggregateForm` + `lib/data/si-suffix.ts` (case-sensitive: lowercase `m` = milli, uppercase `M` = mega per TRD §3.4). Type dropdown sourced from `/aggregation-options` `eligible_types`; submits with `input_mode=convenience` so server preserves the original suffix string in `convenience_input`.
- [x] **P3-17** SSE progress UI — `Queued (#N)` → `Aggregating <YYYY>-<MM> … X%`. Success: column appears, toast with `actual_overshoot_pct`. Failure: `ProblemDetails` rendered. — `JobProgressCard` + `useJobStream` hook. SSE client uses `@microsoft/fetch-event-source` (native EventSource doesn't support `Last-Event-ID` injection from localStorage).
- [x] **P3-18** `localStorage` persistence of `jobId` keyed by `(exchange, asset, feedId)` + `Last-Event-ID` resume. — `useDataJobsStore` with `zustand/middleware`'s `persist` (`partialize` keeps only the `jobs` map; functions never serialized). Composite key `${exchange}|${asset}|${outcomeFeedIdHint}` allows multiple in-flight jobs per asset. `purgeStale` on hydrate drops entries >24h old (server retention is ~10min anyway). 410 Gone on resume → `clearJob` to stop reconnect loops.
- [x] **P3-19** Time-bar EqI yellow banner on the form AND on the built feed's Status card (uses P2b-13 copy). — `lib/data/eqi-banner.ts` `pickEqiBanner(warnings)` filters by the substring `"taker-buy proxy"` and returns the server-supplied string verbatim. Form pulls from `/aggregation-options.warnings`; Status card pulls from same endpoint when manifest's `imbalance_reconstruction_method == "m1_taker_buy_proxy"`. Zero client-side string composition (`eqi-banner.test.tsx` pins this).

---

## Phase 4 — Subscription redesign + run-launch UI

### Type-system foundations

- [x] **P4-1** `TimeFrame` value type — `record struct TimeFrame(TimeSpan)` with `Code`/`Parse`. (TRD §9.1) — `src/AlgoTradeForge.Domain/Strategy/TimeFrame.cs` + `tests/AlgoTradeForge.Domain.Tests/Strategy/TimeFrameTests.cs` (8 tests).
- [x] **P4-2** Migrate every callsite from P0-4 enumeration off raw-`TimeSpan` overloads. — `DataSubscription.TimeFrame` flipped from `TimeSpan` to `TimeFrame`. Implicit `TimeFrame → TimeSpan` operator added (the safe direction; reverse stays explicit) so `Resample(...)`, comparisons, and arithmetic on existing call sites continue to compile. Bulk-rewrote ~150 ctor callsites + 19 `OneMinute`/`FiveMinutes` test fixtures; production sites in `BacktestPreparer`, `StartLiveSessionCommandHandler`, `OptimizationSetupHelper` updated; `DonchianBreakout` / `PrevBarBreakout` strategies + private `ZigZagBreakoutStrategy` use `.Duration.TotalMilliseconds`. Live-API DTO wire shape preserved as `"hh:mm:ss"` via explicit `.Duration.ToString()` to avoid breaking FE consumers.
- [x] **P4-3** Remove raw-`TimeSpan` overloads. (TRD §9.1) — Implicit since the type changed in place rather than added alongside; the only `TimeSpan`-typed surface that survives is the implicit-conversion operator on `TimeFrame` itself. Solution + private + Domain/Application/Infrastructure/WebApi/HistoryLoader tests all pass (1,827 tests across 5 projects).
- [x] **P4-4** Define `DataFeedSubscription` abstract record + `DataFeedKind` + `DataFeedRole` enums. (TRD §9.2) — `DataFeedKind` reused from `Domain/History/DataFeedDescriptor.cs` (P1a-26); new `DataFeedRole { Primary, Side }` and abstract `DataFeedSubscription(AssetName, Exchange, Role)` in `Domain/Strategy/Subscriptions/`.
- [x] **P4-5** Implement `TimeBarSubscription`, `AltBarSubscription`, `TickSubscription`, `SideFeedSubscription`. (TRD §9.2) — each carries its kind-specific payload (TimeFrame / FeedId / nothing / FeedId); `Kind` property is `[JsonIgnore]` per-override (STJ doesn't propagate base attributes to derived overrides).
- [x] **P4-6** Wire polymorphic JSON discriminator via `System.Text.Json`. (TRD §9.2) — `[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]` + four `[JsonDerivedType]` on the base. Built-in since .NET 7, no new package. Added `TimeFrameJsonConverter` so `TimeFrame` round-trips as canonical `Code` ("1m") on the wire — the get-only `Duration` property override defeats STJ's constructor-binding heuristic, so a converter is the right surface.
- [x] **P4-7** Test: polymorphic deserialization round-trip across all four subtypes. — `DataFeedSubscriptionPolymorphismTests` (10 tests): per-subtype round-trip, discriminator name pinning, `Kind` is JsonIgnore'd on wire, missing/unknown discriminator throws, mixed-list deserializes per-element, role serializes as int by default (PR-B's WebApi adds `JsonStringEnumConverter` if string-on-wire is desired).
- [x] **P4-8** Test: `AltBarSubscription.FeedId` matches §3.3 grammar; collision-detection asserts component uniqueness. — `AltBarSubscriptionTests` (4 tests): grammar conformance via `AltBarFeedId.TryParse` (Domain.Tests references HistoryLoader.Domain — Domain production graph stays clean), X-5 ambiguity (`EqV_1m_500m` vs `EqV_5m_500m`), record-equality dedup, distinct roles unequal.

### `IFeedContext` migration

- [x] **P4-9** Migrate `IFeedContext` to span-returning `TryGetLatest`, `HasNewData`, `GetSchema`. Update every impl in public + private repos. (TRD §9.4) — Span migration is `TryGetLatest` + `TryGetPrimarySidecar` (the array-returning pull APIs); `HasNewData` and `GetSchema` already return scalar/object types unaffected by the array→span move. C# 11+ implicit `scoped` on `out` ref-struct parameters means no annotations needed. Public-repo impact: `BacktestFeedContext`, `NullFeedContext`, `BacktestEngine` line 322 (auto-apply consumer), and 4 test files. The `TryGetLatest_ReturnsSameBufferInstance` test reframed as `TryGetLatest_AliasesStableBuffer_AcrossAdvances` using `MemoryMarshal.GetReference` + `Unsafe.AreSame` to preserve the zero-alloc-row-buffer pin behind the span surface. **Private repo confirmed unaffected** (no strategy in `../AlgoTradeForge.Private/` touches `IFeedContext` directly — they all consume `DataSubscription`).

### Engine + command boundary

- [x] **P4-10** Replace `Application.DataSubscriptionDto` with `DataFeedSubscription` at API/command boundary. No back-compat shims. (TRD §1, §9) — `DataSubscriptionDto` deleted; `DataFeedSubscription` threads through every command (`RunBacktestCommand`, `StartDebugSessionCommand`, `StartLiveSessionCommand`, `EvaluateOptimizationQuery`, `RunGenetic*Command`, `RunGroupOptimizationCommand`), every persisted record (`BacktestRunRecord`, `OptimizationRunRecord`), and every WebApi contract. `BacktestPreparer` / `OptimizationSetupHelper` / `StartLiveSessionCommandHandler` throw `NotSupportedException` for non-TimeBar primaries (PR-A scope; PR-C lifts when `HistoryRepository.Load(DataFeedSubscription)` lands). `RunBacktestCommandHandler` persists `command.DataSubscriptions` verbatim so the wire shape survives the resolution boundary. `SqliteRunRepository.FallbackPrimaryFromColumns` picks the right subtype (Tick / TimeBar / AltBar) from the denormalized `timeframe` column for legacy rows. Run-keys use `(int)Role` so persistence is decoupled from JSON enum-converter changes. 2,272 tests green.
- [x] **P4-11** `BacktestInputs` carries `Primary` (Role=Primary, Kind ∈ {TimeBar, AltBar}) + `SideFeeds`. (TRD §9.3) — staged shape uses single ordered `Subscriptions` list (index 0 = primary) with `Primary` / `SideFeeds` convenience accessors; sibling `OptimizationInputs(PrimaryCandidates, SideFeeds)` lands alongside for P4-14 fan-out. Tick is also accepted as a primary kind (P2a-6 monotonic-bump path). `BacktestInputsFormatter` centralizes the `asset/exchange/feed` and `asset:exchange:feed:role` formats consumed by `RunKeyBuilder` + `SimulationCacheBuilder` in PR-3.
- [x] **P4-12** Engine glob resolution per `Kind` (TimeBar / AltBar / Tick / Side-sidecar / Side-top-level). (TRD §9.3) — Glob resolution itself was wired in P1a-26..28; P4-12 lifted the three TimeBar-only `NotSupportedException` guards (BacktestPreparer / OptimizationSetupHelper / live blocked on purpose) by adding `IHistoryRepository.Load(Asset, DataFeedSubscription, ...)` polymorphic overload. New `StrategySubscriptionFactory.FromPrimary` synthesizes a strategy-side `DataSubscription` with placeholder TimeFrame derived from AltBar source code (TRD §3.3 grammar). Optimization gains a dual-key trial carrier (`["FeedSubscriptions"]` alongside `["DataSubscriptions"]`) so AltBar FeedIds round-trip into run records and cache lookups use kind-aware `BacktestInputsFormatter.Key` (regression-tested for `EqV_1m_1000` vs `EqV_1m_5000` non-aliasing). Loader's Side path auto-detects `.flow` suffix per TRD §9.3 (sidecar → `aggregated/<FeedId>/`, top-level → `<FeedId>/`). Live trading kept TimeBar-only with a clarified message (alt-bar live needs the connector aggregator pipeline, post-Phase-6).
- [x] **P4-13** Test: engine glob resolution — sidecar (`Side` + `<feedId>.flow`) routes nested; top-level side feeds (`funding-rate`) route to asset-root glob. — `PartitionedCsvBarLoaderTests` adds `Load_Tick_RoutesToTicksDir`, `Load_Side_Sidecar_RoutesToAggregatedFlowDir`, `Load_Side_TopLevel_RoutesToAssetRootDir`, `Load_Side_Sidecar_DoesNotConflictWithParentBarDir`. `HistoryRepositoryTests` adds the polymorphic dispatch matrix (`LoadFeedSubscription_TimeBar/AltBar/Tick/Side` covers descriptor build + Side rejection + perp asset suffix). New `OptimizationSetupHelperTests` pins the cache-key non-aliasing regression and placeholder-TimeFrame synthesis for AltBar/Tick.
- [x] **P4-14** Optimization: `OptimizationInputs.Subscriptions` (multi-primary; `Role=Primary` entries are fan-out candidates, `Role=Side` are shared side feeds) → fan-out across primaries × parameter grid. `IParameterNormalizer` dedup applies per-primary. (TRD §9.6) — `OptimizationSetupHelper.ExpandMultiPrimary` pre-splits multi-primary DSSes into single-primary DSSes carrying the original `Role=Side` entries. Wired into `RunGroupOptimizationCommandHandler` (brute-force fan-out: one child run + one `ComputeTask` per primary, single `OptimizationGroupRecord`). Per-primary dedup is automatic — each child gets its own `NormalizingEnumerable` (whose `SkippedCount` is per-instance state via `Interlocked`). `EvaluateOptimizationQueryHandler` applies the same expansion so cost-preview's `dssCount` reflects post-expansion fan-out. **Genetic:** scoped down — see P4-14b.
- [ ] **P4-14b** Genetic multi-primary fan-out — _bundled with the FE iteration (P4-17..P4-19)._ Refactor `RunGeneticOptimizationCommandHandler` from single-task enqueue into a per-DSS loop mirroring brute-force; switch the return DTO uniformly to `OptimizationGroupSubmissionDto` (single-DSS case becomes a "group of 1"). Replace the existing `HandleAsync_MultiPrimaryDss_ThrowsNotSupported` test with `_ProducesPerPrimaryChildRuns`. **Fitness-cache audit complete:** `GeneticFitnessCache.Create(...)` is invoked inside `GeneticOptimizationTaskExecutor.ExecuteAsync` (line 106) — cache is per-`ComputeTask`, so per-primary fan-out gets independent search spaces automatically. Zero executor changes needed. The handler refactor is purely structural; the DTO change is a wire-level break, hence the FE coupling.
- [x] **P4-15** Test: optimization fan-out — `|primaries| × |combos|` runs; per-primary normalizer dedup intact. — `OptimizationSetupHelperTests` adds 8 `ExpandMultiPrimary_*` cases (identity / multi-primary with shared side / mixed cardinalities / no-primary throws / equal-value primaries still distinct / side-order preserved). `RunGroupOptimizationGeneticTests` adds 3 brute-force fan-out tests (single DSS multi-primary → 2 child runs, multi-DSS multi-primary cartesian, single-primary identity). `RunGeneticOptimizationCommandHandlerTests` pins multi-primary `NotSupportedException`. `NormalizingEnumerableTests.TwoInstances_DedupCountsAreIndependent` pins per-instance `SkippedCount`. `EvaluateOptimizationQueryHandlerTests.MultiPrimaryDss_DssCountReflectsExpansion` + `_ExpansionPreservesSideForEachChild`.
- [x] **P4-16a** Validation — same `Primary` guard. (TRD §9.6) — `RunValidationCommandHandler` and `RunGroupValidationCommandHandler` both throw `ArgumentException` when an optimization run's persisted `DataSubscriptions` carries ≠ 1 `Role=Primary` entries. After P4-14 expansion every child run is single-primary by construction, so this is defense-in-depth against stale/corrupt records. `RunGroupValidationCommandHandlerTests.HandleAsync_OptimizationRunWithMultiPrimary_ThrowsArgumentException` pins the guard.
- [ ] **P4-16b** Validation — range split server-side (walk-forward / OOS). (TRD §9.6) — _deferred. The current `ValidationTaskExecutor` operates on completed optimization trials via `ValidationPipeline` (verdict aggregation over trial trade P&L), not date-range chunked re-execution. Walk-forward / OOS is a new validation **mode** that does not exist today; lands separately with its own design (new pipeline shape + fidelity-equivalence proof for window splits)._

### Run-launch UI

**Recommended next bundle.** Backend contract is stable post-P4-14/15/16a; frontend can consume it without breaking changes. P4-19 + P4-14b are natural to land in the same PR since both touch the multi-primary launch flow.

- [ ] **P4-17** Backtest/Optimization launch — Primary dropdown sourced from `/api/data/.../feeds`; friendly labels (`EqV/1m:1k`); icons distinguish time vs alt. (TRD §10.2)
- [ ] **P4-18** Side-feed multi-select accepts alt bars. (TRD §10.2)
- [ ] **P4-19** Optimization Primary becomes multi-select chip; estimated run count = `primaries × param_combos` (already correct on the BE — `EvaluateOptimizationQueryHandler` returns `dssCount = post-expansion`, so `dssCount × combos` matches what's enqueued). FE adds the chip UI and submits multi-primary in a single DSS. (TRD §10.2) — _coordinate with **P4-14b** in the same PR if landing genetic multi-primary together; the genetic return-shape switch (`OptimizationSubmissionDto → OptimizationGroupSubmissionDto`) is a wire-level break that's cheaper to absorb here than later._

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

- [x] **X-1** Solution-wide reflective test: no `decimal` in storage layer (walks every type implementing read/write CSV interfaces). Land in Phase 1a. (TRD §11A Cross-cutting, §3.4)
- [x] **X-2** Confirm `HistoryLoader.Application/Aggregation/**` is in scope of the existing CLAUDE.md "Int64 Money Convention" `(long)` cast rule. — _Phase 1b accumulators use `MoneyConvert.ToLong` and `ScaleContext.AmountToTicks` exclusively for decimal→long; raw `(long)` casts only for non-monetary values (record counts in `EqTAccumulator`, threshold integer parsing). No parallel rule introduced._
- [x] **X-3** Existing HistoryLoader registrations (`candle-ext`, `funding-rate`, etc.) flow through the P1a-8 synchronized writer. Audit + migrate. Land in Phase 1a. (TRD §5.3) — confirmed: every registration path goes through `ISchemaManager.EnsureSchema` (the only impl is `FeedSchemaManager`, registered as singleton), so the concurrency upgrade applies uniformly without a separate migration.
- [x] **X-4** Logging audit: every job lifecycle event structured-logged via Serilog. `AggregationWorkerHost` logs Started/Completed/Errored/Cancelled at Info+; `AggregationPipeline` logs completion. Sweep deletions at WARN with absolute path land in Phase 1a's `StartupSweepService`.
- [x] **X-5** Code comment at the §3.3 positional parser warning about `EqV_1m_500m` ambiguity (parser is positional). Land in Phase 1a (P1a-3). — inline comment lives on `AltBarFeedId.TryParse`.

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
| 0 | 5 | 4 (P0-2 skipped) | All before any 1a code |
| 1a | 32 + BAKE | 32; BAKE in flight | One PR; one-week bake before 1b |
| 1b | 44 | 38 done · 6 deferred (HTTP integration tests + sum-site conversion) | Bench gate (P1b-44) before merge |
| 2a | 10 + BAKE | 9 done · P2a-8 deferred (needs real ticks post-BAKE) · BAKE pending merge | One PR for collection (P2a-1..5); 24–48 h bake before aggregator work (P2a-6..10) |
| 2b | 13 | 13 | P0-1/P0-2 decision (DIM vs receiver) — DIMs landed (P0-1 PASS) |
| 3 | 19 | 0 | P3-10 (Q-1 resolved) |
| 4 | 21 (was 19; P4-14 split adds 14b, P4-16 split adds 16b) | 16 done (P4-1..P4-16a); next iteration bundles **P4-17 + P4-18 + P4-19 + P4-14b** (FE launch UI + genetic fan-out); P4-16b walk-forward queued for separate design | P0-3 / P0-4 audits drive P4-2 / P4-9 |
| 5 | 6 | 0 | — |
| 6 | 6 | 0 | If needed |
| X | 5 | 4 (X-3, X-5 in 1a; X-2, X-4 in 1b) | Cross-cutting; land with parent phase |
| Q | 4 | — | Each gates a specific task above |
