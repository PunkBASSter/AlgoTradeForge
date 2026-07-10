# Declarative Data Management: Collection Groups → Reconciler → SQLite Index

**Date:** 2026-07-10
**Status:** Approved design, pending implementation plan
**Scope:** HistoryLoader vertical — frontend Data tab, HistoryLoader Web API, Application/Infrastructure layers, metadata storage.

## 1. Problem

The current data-management vertical does not scale to thousands of tickers and breaks down around lazy (on-demand) heavy feeds:

- **Metadata is a filesystem crawl.** The catalog recursively scans `DataRoot` for every `feeds.json` (`FeedCatalog.ScanAssetDirs`, 10-min in-memory cache). Coverage is worse: `CoverageEndpoints` + `MonthCoverageCalculator` run **uncached on every request** and count rows by reading each monthly CSV line-by-line. At hundreds-to-thousands of assets this is unacceptably slow.
- **Two disconnected configuration models.** Eager collection is declared in a ~880-line hand-maintained `Assets[]` array in `appsettings.json` (invisible to the UI); resampling and archive loads are one-shot imperative forms (`NewAggregateForm`, `ArchiveLoadForm` — the latter driven by a hardcoded feed list). Nothing remembers that a lazy feed *should* exist: to resample from aggTrades the user must manually load the archive first, and that intent is lost after the job finishes.
- **Job state is ephemeral.** Two separate in-memory job registries (archive loads: polled; aggregations: SSE) lose everything on restart; the frontend papers over this with job ids in localStorage.

## 2. Decisions (agreed 2026-07-10)

| # | Decision | Choice |
|---|----------|--------|
| D1 | Configuration model | **Desired-state + reconciler** (not imperative job templates). JSON documents declare what should exist; a reconciler drives reality toward the declaration. |
| D2 | Metadata store | **SQLite as a rebuildable index**, not source of truth. Truth stays on disk (`feeds.json` + data files). **Separate SQLite instance** owned by HistoryLoader (`history-index.sqlite`), never shared with `runs.sqlite`. |
| D3 | Heavy source lifecycle | **Keep source on disk + disk guard** (existing phase-3 guard). Per-feed retention overrides deferred. |
| D4 | On-demand trigger | **UI "Materialize" button first**; automatic trigger from backtest launch is a later phase on top of the same endpoint. |
| D5 | Config granularity | **Named collection groups** as first-class documents (one JSON file per group). Exchange is an **array field inside the group** — desired state expands combinatorially (exchanges × symbols × feeds). |
| D6 | Iteration scope | Declarations govern what HistoryLoader can collect (Binance spot/futures today). The SQLite index covers the **entire** DataRoot including the ~12k-symbol equity catalog (fast catalog/coverage for everything; managed collection only where collectors exist). |
| D7 | Symbol identity | **Canonical symbol grammar** in configs + per-exchange `IExchangeSymbology` mappers (reuses the LiveHost Plan-1 venue-neutral-identity pattern). No per-exchange config schemas. |
| D8 | Storage format | New **`format: csv \| parquet`** axis per feed behind an `IFeedFormat` abstraction. CSV stays for light feeds; Parquet for heavy feeds (aggTrades, future orderbook/options snapshots). Parquet implementation is a later phase, but the schema field and factory seam land from day one. |

## 3. Design

### 3.1 Collection groups

Directory `{ConfigRoot}/groups/*.json`, one file per group. `ConfigRoot` is a new `HistoryLoaderOptions` path (default: sibling of DataRoot, e.g. `%LOCALAPPDATA%/AlgoTradeForge/HistoryConfig`). Git-versioning of declarations is **opt-in**: point `ConfigRoot` at a repo-tracked directory — the default location is not versioned. Written via API with CAS (ETag / `WriteIfMatch`), same discipline as LiveHost `collection.json`.

```jsonc
// groups/crypto-majors.json
{
  "name": "crypto-majors",
  "enabled": true,
  "exchanges": ["BINANCE", "BYBIT"],
  "assets": {
    "symbols": [
      "BTC/USDT",             // spot
      "BTC/USDT-PERP",        // perpetual
      "ETH/USDT-PERP",
      "BTC/USD-FUT-2026-09"   // dated future (grammar reserved)
    ],
    "historyStart": "2020-01"
  },
  "feeds": {
    "candles":      { "collect": "eager", "intervals": ["1m", "1h"] },
    "funding-rate": { "collect": "eager" },
    "liquidations": { "collect": "eager" },
    "agg-trades":   { "collect": "on-demand", "format": "parquet" }
  },
  "derived": {
    "EqV_1k":      { "source": "agg-trades", "type": "EqV", "threshold": "1k", "materialize": "on-demand" },
    "candles_5m":  { "source": "candles", "sourceInterval": "1m", "materialize": "eager" }
  },
  "symbolOverrides": {
    "BYBIT": { "BTC/USDT-PERP": "BTCPERP" }   // optional escape hatch for irregular venue symbols
  }
}
```

**Semantics.**
- Feed keys are the **on-disk feed names** from the `feeds.json` vocabulary: `candles` (one feed, `intervals` array; interval is a filename suffix on disk), hyphenated aux feeds (`funding-rate`, `agg-trades`, `taker-volume`, …), derived feed ids like `EqV_1m_1000`. The group schema parser accepts exactly this vocabulary — no parallel naming convention.
- Effective desired state = **union of all enabled groups**, expanded to concrete `(exchange, canonicalSymbol, feed)` tuples.
- Overlap resolution is deterministic: `eager` beats `on-demand`; `historyStart` takes the minimum. **Non-mergeable conflicts are validation errors**, not merges: the same feed declared with different `format` in two groups (a format change is a data migration, not a merge), or two `derived` entries with the same name but different `source`/`type`/`threshold` — the group store rejects the save with a cross-group validation error naming both groups.
- `historyStart` is a group-wide *upper bound of desire*; per-symbol it is clamped by the discovered actual first month (§3.3) — a 2024 altcoin in a `historyStart: 2020-01` group does not generate requests for 2020 months.
- Feed cadence (cron schedules for kline/funding/OI/…) stays global in the `Schedules` service config — a group declares *membership*, not cadence.
- An exchange without a collector is valid but its tuples surface as `unsupported` in convergence status (forward slot for Bybit/Deribit).
- A one-shot importer converts the current `appsettings.json` `Assets[]` into `groups/legacy-import.json`; `AppSettingsWriter` asset mutation dies (`Schedules`/infra options remain in appsettings).

### 3.2 Canonical symbology

Config symbols use one canonical grammar, parsed once into a structured identity that maps onto the existing `Asset` hierarchy:

| Canonical | Instrument | Asset type |
|---|---|---|
| `BTC/USDT` | spot | `CryptoAsset` |
| `BTC/USDT-PERP` | perpetual | `CryptoPerpetualAsset` |
| `BTC/USD-FUT-2026-09` | dated future | `FutureAsset` |
| `BTC/USD-OPT-…` | option (reserved suffix, not implemented) | — |

Per-exchange mapping behind `IExchangeSymbology` (one implementation per venue, resolved in a single registry by exchange name — `oop-first-design`):

- `BinanceSymbology`: `BTC/USDT-PERP` → API symbol `BTCUSDT` on fapi; catalog directory `BTCUSDT_perp` (formalizes the existing `AssetDirectoryName` / `exchangeSymbolOf` convention — on-disk layout does not change).
- Future venues add a file, not a switch.
- `symbolOverrides` is consulted before the mapper; group validation reports which overrides fired.

This reuses the LiveHost Plan-1 pattern (venue-neutral `Asset` ↔ venue-owned `IbContract`) and matches the industry-proven CCXT unified-symbols approach.

**Deferred:** wildcard expiries for dated futures (e.g. `202X-09`) — the suffix grammar can grow them later without breaking existing configs.

### 3.3 SQLite metadata index — `history-index.sqlite`

Separate instance located alongside DataRoot, owned by `HistoryLoader.Infrastructure`; `HistoryIndexInitializer` modeled on `SqliteDbInitializer` (WAL, `schema_version`, `CREATE TABLE IF NOT EXISTS`).

Tables:
- **`feeds`** — one row per physical feed: exchange, asset_dir, feed_name, interval/threshold, kind, format, first_ts, last_ts, record_count, health, **discovered_first_month**.
- **`month_coverage`** — PK `(feed_id, month)`: rows, expected_rows, complete, file_len, file_mtime.
- **`jobs`** — persistent replacement for both in-memory registries (archive loads + aggregations, plus catalog-rebuild jobs): id, kind, state, progress JSON, timestamps. Jobs survive restarts; frontend localStorage job tracking dies.

**Desired vs observed (spec/status separation).** The machine never writes into group JSON — declarations are user-owned spec; everything the system *discovers* is observed status and lives in the index. Concretely: `BinarySearchStartAsync` date discovery currently persists through `ISettingsWriter` → `AppSettingsWriter`; that path dies with the `Assets[]` array. The discovered actual first month goes to `feeds.discovered_first_month` instead, and the reconciler clamps the group's `historyStart` per symbol against it. (Same principle as K8s `spec`/`status`: the moment the machine mutates spec, CAS discipline and git history of declarations lose meaning.)

**Consistency model.** Disk is truth; the index is derived and rebuildable:
- Incremental: collectors/materializers upsert coverage rows after each month they write (they already know what they wrote); `ISchemaManager.ManifestChanged` re-reads the affected feed row.
- Full rebuild: `POST /catalog/refresh` re-scans everything (CSV via the existing `MonthCoverageCalculator`; Parquet via footer metadata — row count and min/max timestamps come free, no data read). At 12k symbols this is a long crawl, so **refresh is itself a job**: a row in `jobs` with progress + SSE, never a synchronous request.
- Drift sweep: a cheap periodic pass compares `file_len`/`file_mtime` in `month_coverage` against the filesystem and re-scans **only mismatched months** — catches manual file edits without a full rebuild.
- `CoverageEndpoints` and `FeedCatalog` become plain SELECTs. Crawling leaves the hot path entirely, including the 12k-symbol equity catalog. The main WebApi's `DataProxyCache` can be reduced or removed.

### 3.4 Reconciler

A `BackgroundService` in HistoryLoader: expands groups → diffs against the index → publishes per-tuple convergence status:

`unsupported | declared | materializing | materialized | up-to-date | stale | orphaned`

- **Eager tuples:** the reconciler enqueues missing work into the existing execution layer (`BackfillOrchestrator`, aggregation queues). Executors are unchanged; what changes is the source of "what to collect" — collectors consume the desired set derived from the group store instead of `IOptionsMonitor<HistoryLoaderOptions>.Assets`.
- **On-demand tuples:** never fetched automatically. `POST /api/v1/materialize` (optional date range) runs the full chain — archive source download → resample — as one composite job. The backtest-launch auto-trigger later calls this same endpoint.
- **Deletion semantics: data is never deleted automatically.** A feed that exists on disk but is referenced by no enabled group (symbol removed, group disabled, or never declared — e.g. the entire equity root in this iteration) gets status `orphaned` and stays visible in the Explorer. Archive data is expensive and partly irreplaceable (Binance prunes old archive months), so pruning is always an explicit user action, never reconciliation. Note: the existing `DELETE .../feeds/{feedId}` endpoint is restricted to alt-bar feeds (`AggregationEndpoints`: "Only OHLCV_AltBar feeds may be deleted") — extending explicit deletion to orphaned collector-managed feeds is phase-3 scope.
- **Startup sweep + idempotency.** On service start, all `jobs` rows still in a running state are marked `interrupted`; the reconciler then re-enqueues whatever eager work is still missing. The invariant that makes this safe: all collection/materialization is **idempotent** — month-partitioned writes converge on re-run, and the index upsert happens after the file write, so a crash between the two merely leaves the index behind (repaired by drift sweep or rebuild), and duplicated work is harmless.
- **Group-store race:** the reconciler reads an immutable snapshot of the group store, never files mid-`PUT`; the store emits a change event after each successful CAS write, and the reconciler re-reads with debounce.

### 3.5 Storage format axis

`IFeedFormat` (writer + reader + coverage probe), implementations `CsvFeedFormat` / `ParquetFeedFormat`, selected in one factory keyed by the manifest `format` field. Parquet.Net is the only new NuGet dependency, introduced only in the Parquet phase. Heavy-feed benefits: ~5–10× compression vs CSV and free coverage stats from file footers; also the natural home for future orderbook/options-chain snapshots.

### 3.6 Frontend — Data tab v2

Three zones replace the current grid + form sidebar:

1. **Groups** — group cards (name, exchanges, asset count, convergence %, enabled toggle) + group editor: CodeMirror 6 JSON following the `run-new-panel.tsx` pattern, with server-side validation `POST /groups/validate` returning an expansion preview before save ("2 exchanges × 200 symbols × 5 feeds = 2000 feeds, 1400 already materialized; N unsupported; overrides fired: …").
2. **Explorer** — the existing coverage grid, now instant (index SELECTs), with convergence badges; `declared, not materialized` cells get a **Materialize** button.
3. **Jobs** — unified job panel reading the persistent `jobs` table, SSE progress (aggregation-style) for both job kinds.

`ArchiveLoadForm` (hardcoded feed list) and `NewAggregateForm` are deleted — declarations + Materialize replace them.

### 3.7 API surface (HistoryLoader, proxied as today via `/api/data/*`)

- `GET/PUT/DELETE /api/v1/groups/{name}` (CAS via ETag), `GET /api/v1/groups`. Group name is the file name: validated against `^[a-z0-9][a-z0-9_-]{0,63}$` — no path separators, dots, or other filesystem-hostile characters.
- `POST /api/v1/groups/validate` — expansion + symbology-mapping preview
- `GET /api/v1/desired-state` — expanded tuples with convergence status
- `POST /api/v1/materialize` — composite source+derived materialization job
- Existing `loads`/`aggregations`/`catalog`/`coverage` endpoints remain as the execution/read layer (coverage/catalog now index-backed)

## 4. Phasing

1. **Index** — `history-index.sqlite`, incremental updates + full rebuild; catalog/coverage switch to SELECTs. No UX change; independently mergeable; kills the crawl pain immediately.
2. **Groups** — group store + expansion/merge + canonical symbology + appsettings import + reconciler in **dry-run** (status only) + Groups UI and editor.
3. **Reconciler drives collection** — eager feeds sourced from groups; materialize endpoint + button; delete legacy forms; persistent jobs.
4. **Parquet** — `ParquetFeedFormat` for agg-trades (+ footer-based coverage).

## 5. Testing

- Unit: canonical-symbol parser, group expansion/merge/overlap resolution, reconciler diff — all pure functions.
- Contract tests on the index repository (upsert/select/rebuild), following existing SQLite repo test patterns (`Pooling=False`, `ClearAllPools` in Dispose).
- Integration invariant: **full rebuild scan ≡ incrementally maintained index** on the same DataRoot fixture (`HistoryTest`).
- Reconciler idempotency: a second pass over a converged state enqueues nothing.
- Job recovery: simulated crash leaves a `running` job row; startup sweep marks it `interrupted` and the reconciler re-enqueues the missing work exactly once.
- Group validation endpoint: golden tests for expansion previews including `unsupported` exchanges, `symbolOverrides`, and rejected cross-group conflicts (format / derived-name).

## 6. Deferred / out of scope

- Wildcard expiries in dated futures (`202X-09`).
- Explicit deletion of orphaned collector-managed feeds (today's delete endpoint covers alt-bars only; extend in phase 3).
- Asset selectors beyond explicit lists (`top-volume:200`, list files).
- Per-feed source retention overrides (`retainSource: keep | transient | days:N`).
- Backtest-launch auto-materialization (phase after UI trigger; same endpoint).
- Options instruments in the grammar (suffix reserved).
- Declarative management of the equity root (no collectors there; index-only coverage in this iteration).
- Bybit/Deribit collectors (the design leaves them a slot: `exchanges` array + `IExchangeSymbology` + `unsupported` status).
