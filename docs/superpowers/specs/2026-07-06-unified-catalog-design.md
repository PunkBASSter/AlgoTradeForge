# Unified filesystem-driven catalog for the backtest symbol picker

**Date:** 2026-07-06
**Status:** Approved design (pre-implementation)
**Owner:** Andrew

## Problem

Running the ATF WebApi in VS Code debug, the backtest-launch symbol picker shows **only Binance crypto**, even though a US-equity archive (12,325 symbols across NASDAQ/NYSE/NYSEMKT, imported 2026-06-26 in the app's native format) exists on disk. Andrew wants crypto and equity to resolve **equally** in the picker, and wants **future paid data feeds** to drop into the same catalog and appear in the picker with no code change — searchable, not a 12k-item dropdown.

### Why it happens (root cause, verified)

Two independent findings, both confirmed on disk and in code:

1. **The picker is config-driven, not filesystem-driven.** The launch-form symbol selector (`FeedPicker` → `MultiPrimaryPicker`) calls `GET /api/data/exchanges` + `/api/data/exchanges/{ex}/assets`, which the WebApi **proxies to HistoryLoader** (`DataEndpoints.cs`). HistoryLoader's `FeedCatalog` enumerates the **configured `HistoryLoaderOptions.Assets[]` list**, not the filesystem. Equity is invisible there because it is not in that config list. (The filesystem scanner `FileSystemAvailableAssetsProvider` only prefills template defaults in `/api/strategies/available`; it never populates the pickable menu.)

2. **Debug reads a curated crypto-only root.** `.vscode/launch.json` points both `CandleStorage__DataRoot` (WebApi read) and `HistoryLoader__DataRoot` (HistoryLoader write) at `%LocalAppData%\AlgoTradeForge\HistoryTest` — a 20-symbol, ~7.2 GB fast fixture. The full unified catalog lives in the default root `%LocalAppData%\AlgoTradeForge\History` (crypto + all equity). So even after fixing #1, backtest **resolution** (`FileSystemAssetRepository`, which *does* scan `CandleStorage:DataRoot`) needs the root repointed to see equity files.

## Decisions (from brainstorming)

- **Catalog scope:** everything selectable, via search (not a curated watchlist, not a plain dropdown).
- **Freshness:** cached catalog, refreshed on an explicit action or app restart (no background watcher).
- **Catalog owner:** **HistoryLoader** — unify both surfaces (Data page + backtest picker) on one filesystem-driven catalog. Adds no new coupling (the picker already proxies to HistoryLoader) and matches the "HistoryLoader owns data" service-decomposition direction.

## Design

### Guiding seam

HistoryLoader's `FeedCatalog` (`src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs`) is already a cached singleton: `IMemoryCache` with version-suffixed keys, a monotonic `_version` bumped on `ISchemaManager.ManifestChanged`, per-key single-flight `SemaphoreSlim` gates. **We swap only its data source**, keeping the caching plumbing. HistoryLoader already scans `{exchange}/{symbol}` dirs (`StartupSweepService.EnumerateAssetDirs`, `HistoryLoader.Application/Aggregation/StartupSweepService.cs:40-60`) via `IFileStorage.ListKeys` — the pattern to reuse.

**Scan `feeds.json`, not candles.** Every symbol dir contains exactly one `feeds.json` (written by both the stooq importer and HistoryLoader's `FeedSchemaManager`). `IFileStorage.ListKeys(dataRoot, suffix: "feeds.json", recursive: true)` yields ~19k manifest paths (one per asset) instead of walking ~1.4M candle files. Each path gives `{exchange}/{symbol}/feeds.json` → derive exchange + symbol; the manifest already carries `Candles.Intervals` and `Candles.ScaleFactor`. Because freshness is refresh-based, the scan cost is paid on refresh/startup only, never per request.

**Collection config vs. catalog listing get decoupled.** `HistoryLoaderOptions.Assets[]` keeps driving *what gets collected/backfilled* (collectors, `BackfillOrchestrator`, scheduled services). The *catalog* becomes "what's actually on disk." This decoupling is what makes "drop paid data → it appears" true without editing a config array.

### Components

1. **`FeedCatalog` → filesystem source.** In `GetExchanges` / `GetAssetsByExchange` / `GetAllAssets` (via `BuildAssetEntries`), replace the `_options.CurrentValue.Assets` enumeration with the `feeds.json` scan described above. Keep the existing `CachedAsync` / `_version` machinery. `AssetCatalogEntry.Symbol` = dir name; `DisplayName` = symbol without `_perp`; `Type` = classified (see #3); `Feeds`/intervals from the manifest (as today via `manifest.Candles.Intervals` + `manifest.Feeds`).

2. **Explicit refresh.** Add a method on `IFeedCatalog` to bump `_version` (invalidate + clear gates), exposed as `POST /api/v1/catalog/refresh` (`CatalogEndpoints.cs`) and proxied as `POST /api/data/refresh` (`DataEndpoints.cs`). Startup populates lazily on first request (no eager scan needed).

3. **Type classification in HistoryLoader.** HistoryLoader has no crypto/equity/perp inference today (it lives only in the WebApi's `FileSystemAssetRepository.UsEquityExchanges`). Add a small helper in `HistoryLoader.Domain` (alongside `AssetPathConvention`): exchange ∈ `{NASDAQ, NYSE, NYSEMKT, AMEX, ARCA, BATS}` → `equity`; dir ends with `_perp` → `perpetual`; else `spot`. Mirror the WebApi set with a comment cross-referencing it (a shared module is a possible later refinement; avoided now to not introduce cross-project coupling).

4. **Slim list for the picker.** `GET /api/data/assets` returns lightweight entries `(exchange, symbol, display_name, type, intervals)` — no heavy per-feed detail. Per-asset feed detail stays fetched on selection (existing `getAssetsByExchange` / a per-asset call). Keeps the one-time payload small for 12k+ symbols. (Wire format stays snake_case, per the existing proxy convention.)

5. **Searchable picker (frontend).** Build a typeahead combobox (no combobox lib exists today; every selector is a native `<select>`). `FeedPicker` (`frontend/components/features/launch/feed-picker.tsx`) / `MultiPrimaryPicker` fetch the slim list once (TanStack Query, `staleTime` effectively infinite until refresh), filter client-side across `exchange` + `symbol` + `display_name` + `type`. A "refresh catalog" control invalidates the query and calls `POST /api/data/refresh`. Feed selection (third cascade step) is unchanged once an asset is chosen.

6. **Debug roots.** In `.vscode/launch.json`, the default debug profile points both `HistoryLoader__DataRoot` (scan source) and `CandleStorage__DataRoot` (WebApi resolution + bar loading) at `%LocalAppData%\AlgoTradeForge\History`. Keep a separate **"fast fixture (HistoryTest)"** launch profile for quick crypto-only iteration.

7. **Equity scale correctness.** WebApi `FileSystemAssetRepository` (`src/AlgoTradeForge.Infrastructure/History/FileSystemAssetRepository.cs:79-87`) equity branch currently hardcodes 2 decimals and ignores `feeds.json` `Candles.ScaleFactor`. Change it to read `decimalDigits` from the manifest (as the crypto branch already does via `ReadDecimalDigitsFromFeedsJson`), so finer-precision paid equity data round-trips correctly instead of mis-scaling. Today's stooq data is 2-decimal, so this is a no-op for current data and a correctness guard for future feeds.

### Data flow (after change)

```
paid/importer writes {exchange}/{symbol}/candles/YYYY-MM_{interval}.csv + feeds.json
   → (user triggers) POST /api/data/refresh → HistoryLoader bumps _version
   → GET /api/data/assets  → FeedCatalog scans feeds.json keys under DataRoot (cached)
   → frontend combobox filters the slim list client-side
   → user picks symbol → backtest run → FileSystemAssetRepository (scans CandleStorage:DataRoot)
     resolves the authoritative Asset (type + decimalDigits from feeds.json)
```

## Honest caveat (accepted)

Directory names are **lossy**: `AAPL` cannot be distinguished as spot-vs-equity, nor `X_perp` as perpetual-vs-future, from the path alone. The catalog `type` is therefore a **heuristic** (equity-exchange set + `_perp` suffix). This is adequate for listing/filtering. The **authoritative** Asset type and tick size are still resolved at backtest time by `FileSystemAssetRepository`; no runtime behavior depends on the catalog `type` being exact.

## Testing

- **HistoryLoader (unit):** scanner over a temp fixture with mixed crypto / equity / perp dirs, including one dir missing `feeds.json` (must be skipped); refresh picks up a newly-added dir; type classification for each exchange/suffix case.
- **WebApi (unit):** equity `decimalDigits` read from `feeds.json` (non-2-decimal case scales correctly).
- **Frontend (component):** combobox filters the slim list by symbol/exchange/type; refresh control invalidates the query.

## Out of scope (parked)

- **Problem 2 — D1+M5 timeframe exception.** Separate bug in `QuickFlipScalperStrategy.OnInit` (exact-`TimeSpan` match of two Primary feeds) and/or wire-format `D1`/`M5` vs `1d`/`5m` parsing. Deferred; needs the exact exception text to disambiguate.
  - ⚠️ **Related:** `MultiPrimaryPicker` caps at `maxPrimaries={1}`, but QuickFlip needs **two** Primary feeds (5m entry + 1d ATR). That cap is likely entangled with Problem 2 and will be addressed there, not here.
- The `HistoryLoaderOptions.Assets[]` list remains as the **collection** config (backfill scheduling); this change does not touch collectors.

## Key file references

- `src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs` — catalog source swap + refresh
- `src/AlgoTradeForge.HistoryLoader.Application/Catalog/CatalogResponses.cs` — DTOs (slim list entry)
- `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CatalogEndpoints.cs` — `/api/v1/...` + new refresh route
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/StartupSweepService.cs:40-60` — scan pattern to reuse
- `src/AlgoTradeForge.HistoryLoader.Domain/AssetPathConvention.cs` + `AssetTypes.cs` — dir naming; add classification helper nearby
- `src/AlgoTradeForge.Storage.Abstractions/IO/IFileStorage.cs:14` — `ListKeys(prefix, suffix, recursive, ct)`
- `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs` — proxy group `/api/data`, add `/refresh`
- `src/AlgoTradeForge.WebApi/Data/DataProxyCache.cs` — existing proxy cache (2s TTL) to account for
- `src/AlgoTradeForge.Infrastructure/History/FileSystemAssetRepository.cs:79-87,144-147` — equity classification + scale fix
- `frontend/components/features/launch/feed-picker.tsx` — selector to replace with combobox
- `frontend/components/features/dashboard/run-new-panel.tsx` — launch form host (`MultiPrimaryPicker`, `maxPrimaries`)
- `frontend/lib/services/data-api.ts` — `getExchanges` / `getAssetsByExchange` / `getAssets` client
- `frontend/types/data-tab.ts:17-25` — `AssetCatalogEntry` type (extend/slim for the list)
- `.vscode/launch.json` — debug DataRoot profiles (History default + HistoryTest fixture)
