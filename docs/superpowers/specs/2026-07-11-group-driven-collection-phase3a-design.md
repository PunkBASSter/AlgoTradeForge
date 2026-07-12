# Group-Driven Collection — Phase 3a Design

**Date:** 2026-07-11
**Status:** Approved design, pending implementation plan
**Parent spec:** `2026-07-10-declarative-data-management-design.md` (§3.4 reconciler, §4 phasing item 3)
**Scope:** HistoryLoader Application/Infrastructure/WebApi. Frontend untouched except convergence-status vocabulary. Phase 3b (persistent jobs + materialize + FE) gets its own spec.

## 1. Problem

Phase 2 delivered groups as declared desired state and a dry-run reconciler, but collection still runs off the legacy `HistoryLoaderOptions.Assets[]` array: ~15 collector/stream services, `BackfillOrchestrator`, `SymbolCollector`, load endpoints, and the canonicalizer all iterate `AssetCollectionConfig`/`FeedCollectionConfig`. Editing a group changes the convergence report but not what gets collected. Two config models coexist; `DecimalDigits` and date discovery still live in appsettings via the deprecated `ISettingsWriter` write-back.

## 2. Decisions (agreed 2026-07-11)

| # | Decision | Choice |
|---|----------|--------|
| P1 | Phase split | **3a = collection from groups + instrument meta + evaluator fast-follows; 3b = persistent jobs (full registry replacement) + materialize + FE.** 3a merges independently. |
| P2 | Collector input model | **Native transition.** Collectors consume a projection of `DesiredState` directly; `AssetCollectionConfig`/`FeedCollectionConfig` die as the runtime model (kept only as `LegacyGroupImporter` input). No adapter layer. |
| P3 | Price/qty precision source | **Binance `exchangeInfo` → index** (`instrument_meta` table). Observed status, never spec: groups stay machine-free. Recorded on-disk scale always beats exchangeInfo for existing assets. |
| P4 | Date discovery persistence | **`feeds.discovered_first_month`** (reserved since phase 1) replaces `ISettingsWriter`. Evaluator and collectors clamp to it — this is what makes the reconciler kick idempotent. |
| P5 | Archive loads for undeclared symbols | **Rejected (422).** Groups are the only entry point for managed collection; the FE flow is "add to group → collect/load". Deliberate behavior change of `POST /loads`. |

## 3. Design

### 3.1 CollectionPlan — the execution projection

`CollectionPlanBuilder` (Application/Groups) projects `DesiredState.Tuples` into the shape collectors iterate:

- `CollectionAsset` — `Exchange` (lowercase), `Canonical`, `VenueInstrument` (ApiSymbol, AssetType, Dir), `DecimalDigits` (resolved via §3.3; assets with unknown precision are **excluded** from the plan and surfaced as `blocked`, §3.7), `Feeds`.
- `CollectionFeed` — `FeedName`, `Interval`, `Collect` (eager | on-demand), `Format`, `EffectiveStart` (`DateOnly`): `max(tuple.HistoryStart, feeds.discovered_first_month)` joined from the index at plan-build time. HistoryStart lives **only** on the feed — the merged per-feed value from expansion is final; the old `feed.HistoryStart ?? asset.HistoryStart` fallback dies with the config model.

Excluded from the plan: `unsupported` tuples (Venue == null — no collector by definition) and derived tuples (materialization is 3b; they remain visible in convergence only).

**One pipeline, one owner.** `DesiredStateService` owns the whole chain: `GroupsChanged` → 500 ms debounce → rebuild `CollectionPlan` → evaluate convergence → reconciler kick (§3.4) → publish `PlanChanged`. A single event ordering; consumers (collectors read `ICollectionPlanSource.Current`, streams subscribe to `PlanChanged`) can never observe a kick or a resubscribe against a stale plan. There is no second debouncer.

Dead config: `FeedCollectionConfig.Enabled` (removal from a group *is* disable), per-feed `GapThresholdMultiplier` (becomes a single `HistoryLoaderOptions.GapThresholdMultiplier`, default 2.0 — never overridden per-feed in production).

### 3.2 Date discovery → `feeds.discovered_first_month`

`SymbolCollector.CollectWithDateDiscoveryAsync` and `ArchiveBackfillService` currently persist the binary-searched start month via `ISettingsWriter.UpdateFeedHistoryStart`. Both switch to `IHistoryIndex.SetDiscoveredFirstMonth(exchange, dir, feedName, interval, month)` (upsert — the feed row may not exist before first data write). `ISettingsWriter`/`AppSettingsWriter` are deleted.

Consumers of the discovered month:

- **`ConvergenceEvaluator`**: `expected = CountExpectedMonths(max(tuple.HistoryStart, discoveredFirstMonth), nowFirst)`. For non-candles tuples the discovery is read across all index rows matching the feedName (same any-interval rule as coverage); the clamp uses the **earliest** discovered month across intervals — a month counts as expected if *any* interval could have data. No discovery recorded → no clamp.
- **`CollectionPlanBuilder`**: `EffectiveStart` (§3.1) — collectors and kicks start where data can actually exist.

**Discovery feeds back into the pipeline.** Discovery also fires in ordinary scheduled cycles, not just kicked backfills — and the plan's `EffectiveStart` is joined at build time. So `SetDiscoveredFirstMonth` triggers the same debounced pipeline (§3.1); otherwise the plan keeps `EffectiveStart = historyStart` until the next group edit and every cadence cycle re-burns the binary search. The event is rare (new symbols/feeds only) and the debounce collapses a cycle's batch of writes. This is the replacement for the feedback loop `ISettingsWriter` + appsettings hot-reload used to close.

Without this, a symbol listed in 2023 inside a `historyStart: 2020-01` group is eternally `partial` (covered can never reach expected), and the reconciler kick re-enqueues it on every recompute — the convergence invariant ("second pass over a converged state enqueues nothing") is unprovable. The clamp is load-bearing, not an optimization.

### 3.3 Instrument meta — `instrument_meta` table

New index table: `instrument_meta(exchange, dir, price_decimals, qty_decimals, tick_size, fetched_at)`, PK `(exchange, dir)`.

- **Fetch:** `InstrumentMetaProvider` calls exchangeInfo once per venue class (spot `/api/v3/exchangeInfo`, futures `/fapi/v1/exchangeInfo` — each returns every symbol in one response) and upserts all rows. Refresh daily and on cache miss.
- **Decimals derivation:** from the `PRICE_FILTER` `tickSize` (and `LOT_SIZE` `stepSize` for qty), **not** `pricePrecision` — futures `pricePrecision` is the API field width (often 2 while tickSize is 0.10), and spot has no precision fields at all. `tickSize` arrives as a string with trailing zeros (`"0.01000000"`); parsing is unit-tested.
- **Disk wins, loudly.** If the asset dir already has a recorded scale (`feeds.json` candle config, written by `EnsureCandleConfig` as `10^decimalDigits`), the recorded scale governs all writes forever — appending at a different scale corrupts existing CSVs. exchangeInfo seeds **new** assets only. A disk-vs-exchangeInfo divergence is logged as a warning with both values (drift diagnostics — the venue changed tickSize), never silently swallowed.
- **Negative caching.** A symbol absent from a fresh exchangeInfo response is remembered as absent in the provider (in-memory, tied to the last fetch timestamp) until the next daily refresh — a typo'd group symbol must not hammer exchangeInfo on every plan rebuild. A restart costs at most one refetch.
- **Refusal over defaulting.** Unknown precision (not cached, fetch failed) excludes the asset from the plan with a loud log — a silent `2` is quiet scale corruption. The refusal is retryable (next plan rebuild / daily refresh retries the fetch) and visible in the convergence report as status `blocked` (§3.7), distinct from `missing`.

The `CanonicalizerOptions` instrument→digits map dies; the canonicalizer reads `instrument_meta`. Its sibling instrument→assetDir map dies too — `Venue.Dir` from the plan (symbology) is the single source of directory names.

### 3.4 Reconciler kick

Collection already *is* backfill — `ScheduledCollectorService.CollectCycleAsync` walks from the start month every cycle, skipping complete months. Switching the source to the plan therefore collects new group members on the next cadence tick. The kick removes the wait:

- After each convergence evaluation, `DesiredStateService` collects eager tuples with status `missing`/`partial`, groups them by asset, and calls `BackfillOrchestrator.RunAsync` for the affected assets (feed-filtered). Dedup across automatic paths: a per-(assetDir, feed, interval) skip-if-busy gate at the `SymbolCollector.CollectFeed` choke point serializes-by-skip any two concurrent collections of the same feed (scheduled cycle vs kick vs manual backfill); `_runningSymbols` remains the asset-level backfill dedup.
- **Shared concurrency gate:** `BackfillOrchestrator` currently news a `SemaphoreSlim(MaxBackfillConcurrency)` per `RunAsync` call, so overlapping kicks multiply effective concurrency to N×3. The semaphore becomes a single instance field.
- **Recompute on completion:** a kicked backfill's completion triggers the same debounced pipeline (data writes don't fire `GroupsChanged`), so the report catches up without a group save and the idempotency loop closes: kick → collect → recompute → second recompute enqueues nothing.
- **Boot sweep, by design:** the first compute after startup kicks every missing/partial eager tuple. This is deliberate reconciliation-on-boot; complete-month skipping and the circuit breaker keep it cheap.
- **Kick fingerprint — convergence on unfillable holes.** Some tuples are *permanently* partial: a delisted symbol (expected grows every month, covered froze forever) or a permanent archive hole (a month absent from both the archive and REST depth — phase 2 records ANY missing slot). Without a guard the loop kick → futile collect → recompute → still partial → kick spins forever. Fix: at kick time the service snapshots a per-asset fingerprint — the (covered, expected) pairs of the tuples it is kicking. A subsequent kick for the same asset is **skipped while the fingerprint is unchanged**. One futile pass per state is correct (the archive may have replenished); the second is not enqueued, so the loop reaches no-op in one iteration even on holes that can never fill. The fingerprint resets on `GroupsChanged` (editing a group is a legitimate retry) — and any covered/expected movement changes it naturally. Delisting proper (end-of-life date in symbology/index, expected capped at `min(now, delistMonth)`) is deferred (§5); the fingerprint unblocks without it.

### 3.5 Streams

`LiquidationStreamService`, `BookTickerStreamService`, `SpotAggTradeStreamService` take their symbol sets from `ICollectionPlanSource.Current` and resubscribe on **`PlanChanged`** (not raw `GroupsChanged` — the plan must be rebuilt before consumers read it). The existing hot-reload resubscribe mechanics are kept; only the trigger and the source change.

### 3.6 Death of `Assets[]`

Every runtime consumer moves to the plan or the index: `FeedCatalog`, `BinanceLoadAssetResolver`, `LoadEndpoints`/`BackfillEndpoints`/`CoverageEndpoints`/`StatusEndpoints`, `AggregationWorkerHost`/`AggregationEndpoints`, `HistoryLoaderOptionsValidator` (asset-level rules drop; `GroupValidator` owns them), `CanonicalizerOptions`. Per P5, `POST /loads` for a symbol not declared in any enabled group returns 422 `{error: "symbol_not_declared"}`.

`HistoryLoaderOptions.Assets` survives **only** as `LegacyGroupImporter` input (terse comment saying so) — the production first-boot import has not run yet. After import, the appsettings array is dead weight the operator may delete by hand. `ISettingsWriter` dies (§3.2).

### 3.7 Evaluator fast-follows (ledgered from phase 2)

- **`COLLATE NOCASE`** on `exchange` and `dir` predicates in `SqliteHistoryIndex` — removes the lowercase-by-construction caveat at `ConvergenceEvaluator` (feedKeysCache comment, `ToLowerInvariant` dance in the orphan loop).
- **Orphan scan in one query:** `IHistoryIndex.ListAllFeedKeys(ct)` → `(Exchange, Dir, FeedName, Interval)` across the whole index; replaces the per-asset `ListFeedKeys` N+1.
- **Validator rule:** a `derived` key colliding with any `DeclarableFeeds` name is a validation error (post-F1 feedName-wide claiming makes the collision toxic).
- **Stream-feed coverage.** For stream-fed feeds (`liquidations`, `book-ticker` — feeds whose only live source is a WebSocket stream): with observed months, `expected` counts from `max(historyStart, first observed month)` — pre-observation history is unobtainable, not missing. With **zero** observed months the tuple gets a new status **`awaiting-data`** — visible as its own chip, never vacuously `materialized` (a stream that never started is a problem, not convergence).
- **Status vocabulary** becomes: `unsupported | on-demand | blocked | awaiting-data | missing | partial | materialized`. Rule order: unsupported → blocked (asset excluded from plan, §3.3) → on-demand → awaiting-data (stream feed, 0 observed) → missing → materialized → partial. FE renders `blocked` as an error chip, `awaiting-data` as a warning chip; neither counts as missing.

## 4. Testing

- `CollectionPlanBuilder`: tuple grouping by (exchange, venue); derived/unsupported excluded; unknown-precision assets excluded and reported `blocked`; `EffectiveStart` = max(historyStart, discovered) incl. "no discovery → no clamp".
- `ConvergenceEvaluator`: expected clamps to `discovered_first_month` (fixture where group historyStart predates listing — without the clamp the idempotency test is falsely green); stream feed with rows → expected from first observed; stream feed with zero rows → `awaiting-data`; `blocked` ordering.
- `InstrumentMetaProvider`: tickSize/stepSize → decimals incl. trailing-zero strings; disk-wins with divergence warning; negative cache (absent symbol doesn't refetch until refresh); refusal retryable.
- `ScheduledCollectorService.CollectCycleAsync` over the plan: FuturesOnly via `Venue.AssetType`; starts at `EffectiveStart`.
- Reconciler kick: converged state → second recompute enqueues nothing (the §5 invariant of the parent spec, now provable); kick dedupes against a running backfill; completion triggers recompute; concurrent `RunAsync` calls share one semaphore (effective concurrency stays `MaxBackfillConcurrency`).
- Kick fingerprint: an unfillable hole (fixture where a middle month can never fill) is kicked exactly once — the second recompute skips the asset (fingerprint unchanged); a `GroupsChanged` resets the fingerprint and re-kicks; covered/expected movement re-kicks.
- Discovery feedback: `SetDiscoveredFirstMonth` from a scheduled (non-kicked) cycle triggers a plan rebuild — the next cycle starts at the discovered month, no repeated binary search.
- Index contract tests: `instrument_meta` upsert/select, `SetDiscoveredFirstMonth` upsert-before-data, `ListAllFeedKeys`, NOCASE predicates (mixed-case exchange/dir rows).
- Live smoke: add a new symbol to a group → collection starts without manual backfill, scale seeded from exchangeInfo, convergence recomputes after the kicked backfill completes.

## 5. Out of scope (phase 3b spec)

Persistent jobs as a **full replacement** of both in-memory registries (decision recorded: SQLite-backed unified registry, poll + SSE contracts preserved at the endpoint level; startup interrupted-sweep must distinguish "job died" from "killed mid-write with partial months on disk" — the index's partial-month rows already carry that); `POST /api/v1/materialize` composite job (derived: source archive-load → aggregation; on-demand collected: archive-load); FE Materialize button, unified Jobs panel, deletion of `ArchiveLoadForm`/`NewAggregateForm`, death of localStorage job tracking. Also deferred: Parquet (phase 4), backtest-launch auto-materialize, explicit deletion of orphaned collector-managed feeds, delisting end-of-life dates (symbology/index field capping `expected` at `min(now, delistMonth)` — the kick fingerprint keeps permanently-partial tuples from looping without it).
