# Tasks: History Loader — Binance Futures Vertical Slice

**Input**: Design documents from `/specs/019-history-loader/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/api.md

**Tests**: Included per Constitution Principle II (Test-First). Tests for core components (writers, loaders, rate limiter, API clients) are embedded in implementation tasks. Each foundational and US task should include unit tests alongside implementation.

**Organization**: Tasks grouped by user story. Each story is independently implementable after Phase 2 completes.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: Project scaffolding — create project files, register in solution, minimal host skeleton

- [ ] T001 Create `src/AlgoTradeForge.HistoryLoader/AlgoTradeForge.HistoryLoader.csproj` using `Microsoft.NET.Sdk.Web` targeting `net10.0` with project reference to `AlgoTradeForge.Application`. Add Serilog packages (`Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`). Register in `AlgoTradeForge.slnx`
- [ ] T002 [P] Create `tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj` targeting `net10.0` with references to `AlgoTradeForge.HistoryLoader`, xUnit, and NSubstitute. Register in `AlgoTradeForge.slnx`. Add `InternalsVisibleTo` from HistoryLoader to test project
- [ ] T003 Create `src/AlgoTradeForge.HistoryLoader/Program.cs` with minimal ASP.NET Core host: Serilog from config, `builder.Services.Configure<HistoryLoaderOptions>()`, health check endpoint, `app.Run()`. No collection services yet — just a bootable host
- [ ] T004 [P] Create `src/AlgoTradeForge.HistoryLoader/appsettings.json` with full `HistoryLoader` config section structure: `DataRoot`, `MaxBackfillConcurrency`, `Binance` subsection (SpotBaseUrl, FuturesBaseUrl, MaxWeightPerMinute, WeightBudgetPercent, RequestDelayMs), and sample `Assets` list with one BTCUSDT_fut and one BTCUSDT spot entry per `data-model.md` schema

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core shared components that ALL user stories depend on — config records, rate limiter, CSV writers, status tracking, shared helpers

**CRITICAL**: No user story work can begin until this phase is complete

- [ ] T005 Implement `HistoryLoaderOptions`, `BinanceOptions`, `AssetCollectionConfig`, `FeedCollectionConfig` config records in `src/AlgoTradeForge.HistoryLoader/HistoryLoaderOptions.cs` per data-model.md schema. All records use `init` properties with sensible defaults
- [ ] T006 [P] Implement `BinanceIntervalMap` in `src/AlgoTradeForge.HistoryLoader/Binance/BinanceIntervalMap.cs` — static methods `ToIntervalString(TimeSpan)` and `ToTimeSpan(string)` for bidirectional mapping. Port logic from existing `BinanceAdapter.ToIntervalString()`. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceIntervalMapTests.cs`
- [ ] T007 Implement `WeightedRateLimiter` in `src/AlgoTradeForge.HistoryLoader/RateLimiting/WeightedRateLimiter.cs` — thread-safe sliding window tracking total API weight per minute. `AcquireAsync(int weight, CancellationToken)` blocks when rolling weight sum would exceed budget. Use `lock` for window management + `SemaphoreSlim` for async wait. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/RateLimiting/WeightedRateLimiterTests.cs` covering: concurrent access, weight budget enforcement, window sliding, cancellation
- [ ] T008 [P] Implement `SourceRateLimiter` in `src/AlgoTradeForge.HistoryLoader/RateLimiting/SourceRateLimiter.cs` — wraps `WeightedRateLimiter` scoped to a specific base URL. Both spot and futures source limiters feed into the global limiter. Add test in `tests/AlgoTradeForge.HistoryLoader.Tests/RateLimiting/SourceRateLimiterTests.cs`
- [ ] T009 [P] Implement `KlineRecord` and `FeedRecord` internal record structs in `src/AlgoTradeForge.HistoryLoader/Binance/Records.cs`. `KlineRecord`: TimestampMs, Open, High, Low, Close, Volume, QuoteVolume, TradeCount, TakerBuyVolume, TakerBuyQuoteVolume. `FeedRecord(long TimestampMs, double[] Values)`
- [ ] T010 Implement `CandleCsvWriter` in `src/AlgoTradeForge.HistoryLoader/Storage/CandleCsvWriter.cs` — writes int64 OHLCV to `{assetDir}/candles/{YYYY-MM}_{interval}.csv`. Header: `ts,o,h,l,c,vol`. Uses `MoneyConvert.ToLong()` with configurable `DecimalDigits` multiplier. Handles: monthly partition switch, dedup via `_lastWrittenTimestamp`, `FileMode.Append` + `FileShare.Read`, resume from existing file. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/CandleCsvWriterTests.cs` covering: partition creation, header on new file, int64 encoding, month boundary, dedup, resume
- [ ] T011 [P] Implement `FeedCsvWriter` in `src/AlgoTradeForge.HistoryLoader/Storage/FeedCsvWriter.cs` — writes double-valued aux feeds to `{assetDir}/{feedName}/{YYYY-MM}[_{interval}].csv`. Header: `ts,{columns...}` from `FeedDefinition.Columns`. Timestamp as int64 epoch ms, values as double with `InvariantCulture`. Same partition/dedup/append pattern as `CandleCsvWriter`. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedCsvWriterTests.cs`
- [ ] T012 Implement `FeedSchemaManager` in `src/AlgoTradeForge.HistoryLoader/Storage/FeedSchemaManager.cs` — reads/writes/updates `feeds.json` per asset directory. Uses `System.Text.Json` to serialize `FeedMetadata`. `EnsureSchema(assetDir, feedName, definition)` creates or updates the schema atomically. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedSchemaManagerTests.cs`
- [ ] T013 [P] Implement `FeedStatus` model and `FeedStatusManager` in `src/AlgoTradeForge.HistoryLoader/State/FeedStatus.cs` and `src/AlgoTradeForge.HistoryLoader/State/FeedStatusManager.cs` — per-feed status persistence as `{assetDir}/{feedName}/status.json`. Follows `IngestionStateManager` atomic-write pattern (`.tmp` → `File.Move`). Fields: FeedName, Interval, FirstTimestamp, LastTimestamp, LastRunUtc, RecordCount, Gaps[], derived Health. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/State/FeedStatusManagerTests.cs`
- [ ] T014 [P] Implement `AssetDirectoryName` static helper in `src/AlgoTradeForge.Infrastructure/History/AssetDirectoryName.cs` — `From(Asset asset) → string` mapping: `CryptoPerpetualAsset`/`FutureAsset` → `"{Name}_fut"`, `CryptoAsset`/`EquityAsset` → `"{Name}"`. Add tests in `tests/AlgoTradeForge.Infrastructure.Tests/History/AssetDirectoryNameTests.cs`
- [ ] T015 [P] Extend `FeedMetadata` in `src/AlgoTradeForge.Domain/History/FeedMetadata.cs` — add `CandleConfig` class (`decimal Multiplier`, `string[] Intervals`) and `CandleConfig? Candles` property to `FeedMetadata`. This matches the `feeds.json` schema from the requirements doc which has a top-level `"candles"` section alongside `"feeds"`

**Checkpoint**: Foundation ready — all shared components tested. User story implementation can begin.

---

## Phase 3: User Story 1 — Backfill Tier 1 Futures History (Priority: P1) MVP

**Goal**: Backfill complete OHLCV klines and funding rates from Binance Futures API for configured symbols. Write to new CSV format with `feeds.json` schema.

**Independent Test**: Configure BTCUSDT perpetual, trigger backfill, verify candles + funding rate CSVs written correctly from 2019-09 to present.

### Implementation

- [ ] T016 [US1] Implement `BinanceFuturesClient` class in `src/AlgoTradeForge.HistoryLoader/Binance/BinanceFuturesClient.cs` with constructor taking `HttpClient` + `BinanceOptions` + `SourceRateLimiter`. Implement `FetchKlinesAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<KlineRecord>` — paginated `/fapi/v1/klines` with limit=1500, cursor advancement, weight acquisition via rate limiter, exponential backoff on HTTP 429, retry on 5xx (3 attempts), throw on 418. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientKlineTests.cs` with `FakeHandler` for response parsing, pagination, error handling
- [ ] T017 [P] [US1] Add `FetchFundingRatesAsync(symbol, from, to, ct) → IAsyncEnumerable<FeedRecord>` to `BinanceFuturesClient` in `src/AlgoTradeForge.HistoryLoader/Binance/BinanceFuturesClient.cs` — paginated `/fapi/v1/fundingRate` with limit=1000. Returns `FeedRecord` with `[rate, mark_price]` values. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientFundingTests.cs`
- [ ] T018 [US1] Implement `SymbolCollector` in `src/AlgoTradeForge.HistoryLoader/Collection/SymbolCollector.cs` — orchestrates per-symbol, per-feed collection. `CollectFeedAsync(assetConfig, feedName, from, to, ct)` dispatches to the appropriate client method, writes via `CandleCsvWriter` or `FeedCsvWriter`, updates `FeedStatus` and `FeedSchemaManager`. During kline collection, simultaneously write extended fields (QuoteVolume, TradeCount, TakerBuyVolume, TakerBuyQuoteVolume) from `KlineRecord` to `FeedCsvWriter` in `candle-ext/` directory — prevents data loss if US4 is deployed after initial backfill. Handles gap detection during streaming (non-monotonic timestamp jumps > expected interval). Start with kline + funding-rate + candle-ext support; other feeds added in later stories
- [ ] T019 [US1] Implement `BackfillOrchestrator` in `src/AlgoTradeForge.HistoryLoader/Collection/BackfillOrchestrator.cs` — manages backfill lifecycle. `RunAsync(symbols, feeds?, fromDate?, ct)` processes symbols with bounded parallelism via `SemaphoreSlim(MaxBackfillConcurrency)`. Each symbol: resolve configured feeds → determine resume point from `FeedStatus.LastTimestamp` → call `SymbolCollector.CollectFeedAsync()` → save status. Supports both startup backfill and on-demand trigger
- [ ] T020 [US1] Implement `KlineCollectorService` in `src/AlgoTradeForge.HistoryLoader/Collection/KlineCollectorService.cs` — `BackgroundService` with daily `PeriodicTimer`. On each tick: iterates configured futures symbols, calls `SymbolCollector` for candle catch-up (last timestamp → now). Uses `IOptions<HistoryLoaderOptions>` (upgraded to `IOptionsMonitor` in US5/T044)
- [ ] T021 [P] [US1] Implement `FundingRateCollectorService` in `src/AlgoTradeForge.HistoryLoader/Collection/FundingRateCollectorService.cs` — `BackgroundService` with 8-hour `PeriodicTimer`. Same pattern as `KlineCollectorService` but for funding-rate feed only
- [ ] T022 [US1] Register US1 services in `src/AlgoTradeForge.HistoryLoader/Program.cs`: `AddHttpClient<BinanceFuturesClient>()`, `AddSingleton<WeightedRateLimiter>` (global), `AddSingleton<SourceRateLimiter>` (keyed for futures URL), `AddSingleton<SymbolCollector>`, `AddSingleton<BackfillOrchestrator>`, `AddHostedService<KlineCollectorService>`, `AddHostedService<FundingRateCollectorService>`, wire CandleCsvWriter + FeedCsvWriter + FeedSchemaManager + FeedStatusManager as singletons

**Checkpoint**: US1 complete — can backfill OHLCV klines + funding rates for futures symbols. Data written in new format with feeds.json. Resumable after restart.

---

## Phase 4: User Story 2 — Forward Collection of Time-Limited Data (Priority: P2)

**Goal**: Automatically collect 30-day-limited Tier 2 data (open interest, long/short ratios, taker volume) on periodic schedules before it expires from the Binance API.

**Independent Test**: Start service, wait for one collection cycle, verify OI (5m), L/S ratios (15m), and taker volume (15m) CSVs contain correct data.

### Implementation

- [ ] T023 [P] [US2] Add `FetchOpenInterestAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<FeedRecord>` to `BinanceFuturesClient` — `/futures/data/openInterestHist`, limit=500, returns `[oi, oi_usd]`. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientOiTests.cs`
- [ ] T024 [P] [US2] Add `FetchGlobalLongShortRatioAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<FeedRecord>` and `FetchTopAccountRatioAsync(...)` to `BinanceFuturesClient` — `/futures/data/globalLongShortAccountRatio` and `/futures/data/topLongShortAccountRatio`, returns `[long_pct, short_pct, ratio]`. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientRatioTests.cs`
- [ ] T025 [P] [US2] Add `FetchTakerVolumeAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<FeedRecord>` to `BinanceFuturesClient` — `/futures/data/takeBuySellVol`, returns `[buy_vol_usd, sell_vol_usd, ratio]`. Add test in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientTakerTests.cs`
- [ ] T026 [US2] Extend `SymbolCollector` in `src/AlgoTradeForge.HistoryLoader/Collection/SymbolCollector.cs` — add dispatch cases for `open-interest`, `ls-ratio-global`, `ls-ratio-top-accounts`, `taker-volume` feed names to the appropriate `BinanceFuturesClient` methods
- [ ] T027 [US2] Implement `OiCollectorService` in `src/AlgoTradeForge.HistoryLoader/Collection/OiCollectorService.cs` — `BackgroundService` with 5-minute `PeriodicTimer`. Iterates configured symbols, calls `SymbolCollector` for `open-interest` feed
- [ ] T028 [P] [US2] Implement `RatioCollectorService` in `src/AlgoTradeForge.HistoryLoader/Collection/RatioCollectorService.cs` — `BackgroundService` with 15-minute `PeriodicTimer`. Collects `ls-ratio-global`, `ls-ratio-top-accounts`, and `taker-volume` feeds for all symbols in one cycle
- [ ] T029 [US2] Register US2 services in `src/AlgoTradeForge.HistoryLoader/Program.cs`: `AddHostedService<OiCollectorService>`, `AddHostedService<RatioCollectorService>`

**Checkpoint**: US2 complete — Tier 2 feeds collected on schedule. Data preserved before 30-day API window expires.

---

## Phase 5: User Story 3 — Backtest Engine Consumption (Priority: P3)

**Goal**: Enable the backtest engine to load collected data (OHLCV + aux feeds) from the new storage format via `feeds.json` schema, using the existing `IFeedContext` mechanism.

**Independent Test**: Write synthetic test data in new format, run a backtest, verify strategy receives feed values via `IFeedContext.TryGetLatest()`.

### Implementation

- [ ] T030 [P] [US3] Implement `NewFormatBarLoader` in `src/AlgoTradeForge.Infrastructure/History/NewFormatBarLoader.cs` — `IInt64BarLoader` implementation for new CSV format. Path: `{dataRoot}/{exchange}/{assetDir}/candles/{YYYY-MM}_{interval}.csv`. Parses `ts,o,h,l,c,vol` where ts is epoch ms and all values are `long`. Iterates monthly partitions from `from` to `to`, skips header rows starting with `ts`. `GetLastTimestamp()` scans partition files in reverse. Add tests in `tests/AlgoTradeForge.Infrastructure.Tests/History/NewFormatBarLoaderTests.cs` covering: single month, multi-month, date filtering, interval in filename, resume detection
- [ ] T031 [P] [US3] Implement `CsvFeedSeriesLoader` in `src/AlgoTradeForge.Infrastructure/History/CsvFeedSeriesLoader.cs` — loads auxiliary feed CSV files into `FeedSeries(long[], double[][])`. Path: `{dataRoot}/{exchange}/{assetDir}/{feedName}/{YYYY-MM}[_{interval}].csv`. Parses `ts,{columns...}` where ts is `long` and values are `double`. Concatenates monthly partitions within `[from, to]` range. Returns `null` if no files found. Add tests in `tests/AlgoTradeForge.Infrastructure.Tests/History/CsvFeedSeriesLoaderTests.cs`
- [ ] T032 [US3] Create `IFeedContextBuilder` interface in `src/AlgoTradeForge.Application/Abstractions/IFeedContextBuilder.cs` — `BacktestFeedContext? Build(string dataRoot, Asset asset, DateOnly from, DateOnly to)`
- [ ] T033 [US3] Implement `FeedContextBuilder` in `src/AlgoTradeForge.Infrastructure/History/FeedContextBuilder.cs` — reads `{assetDir}/feeds.json` → `FeedMetadata`, for each `FeedDefinition`: loads CSV via `CsvFeedSeriesLoader`, maps `FeedDefinition` → `DataFeedSchema` (parsing `AutoApplyDefinition.Type` → `AutoApplyType` enum → `AutoApplyConfig`), registers with `BacktestFeedContext.Register(feedKey, schema, series, asset)`. Returns `null` if no `feeds.json` exists. Add test in `tests/AlgoTradeForge.Infrastructure.Tests/History/FeedContextBuilderTests.cs`
- [ ] T034 [US3] Update `HistoryRepository` in `src/AlgoTradeForge.Infrastructure/History/HistoryRepository.cs` — use `AssetDirectoryName.From(subscription.Asset)` instead of `subscription.Asset.Name` when passing to `barLoader.Load()`. This maps `CryptoPerpetualAsset` → `"BTCUSDT_fut"` directory
- [ ] T035 [US3] Update `BacktestPreparer` in `src/AlgoTradeForge.Application/Backtests/BacktestPreparer.cs` — inject `IFeedContextBuilder` via constructor. After loading bar data, call `feedContextBuilder.Build(dataRoot, asset, from, to)`. If result is non-null, pass as `FeedContext` in `BacktestSetup`. Update existing test in `tests/AlgoTradeForge.Application.Tests/` to verify `FeedContext` population
- [ ] T036 [US3] Update `src/AlgoTradeForge.WebApi/Program.cs` — replace `CsvInt64BarLoader` registration with `NewFormatBarLoader`. Add `CsvFeedSeriesLoader` and `FeedContextBuilder` as `IFeedContextBuilder` singleton. Update `CandleStorageOptions.DataRoot` default to match new `"History"` root path

**Checkpoint**: US3 complete — backtest engine loads candles + aux feeds from new format. Strategies access feeds via `IFeedContext.TryGetLatest()`. Auto-apply works for funding rates.

---

## Phase 6: User Story 4 — Mark Price and Extended Kline Data (Priority: P4)

**Goal**: Collect mark price klines (1h) and extended kline data (quote volume, trade count, taker volumes) to enable basis calculations and trade flow analysis.

**Independent Test**: Backfill mark price for one symbol at 1h, verify CSV contains OHLC mark prices. Verify candle-ext CSV has taker volumes aligned with candle timestamps.

### Implementation

- [ ] T037 [P] [US4] Add `FetchMarkPriceKlinesAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<KlineRecord>` to `BinanceFuturesClient` — `/fapi/v1/markPriceKlines`. Same pagination as klines but returns mark price OHLC (volume always 0). Add test in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientMarkPriceTests.cs`
- [ ] T038 [US4] Extend `SymbolCollector` — add `mark-price` feed dispatch (fetches via `FetchMarkPriceKlinesAsync`, writes OHLC columns to `FeedCsvWriter` as double values). Verify `candle-ext` writing (already added in T018) produces correct columns aligned with candle timestamps
- [ ] T039 [US4] Implement `HourlyCollectorService` in `src/AlgoTradeForge.HistoryLoader/Collection/HourlyCollectorService.cs` — `BackgroundService` with 1-hour `PeriodicTimer`. Collects `mark-price` feed (and later `ls-ratio-top-positions` from US6)
- [ ] T040 [US4] Register `HourlyCollectorService` in `src/AlgoTradeForge.HistoryLoader/Program.cs`

**Checkpoint**: US4 complete — mark price and candle-ext data collected. Basis spread computable from mark-price + candles feeds.

---

## Phase 7: User Story 5 — Configuration and Monitoring (Priority: P5)

**Goal**: HTTP endpoints for status queries, on-demand backfill triggers, health checks, and hot-reload of symbol configuration.

**Independent Test**: Modify symbol config while service runs, verify new symbol picked up. Query status endpoint, verify correct feed timestamps and gap counts.

### Implementation

- [ ] T041 [P] [US5] Implement API request/response models in `src/AlgoTradeForge.HistoryLoader/Endpoints/Models.cs` — `BackfillRequest`, `BackfillResponse`, `StatusResponse`, `SymbolStatus`, `FeedStatusSummary`, `SymbolDetailResponse` per contracts/api.md
- [ ] T042 [US5] Implement `StatusEndpoints` in `src/AlgoTradeForge.HistoryLoader/Endpoints/StatusEndpoints.cs` — `GET /api/v1/status` returns all symbols with feed summaries, `GET /api/v1/status/{symbol}` returns detailed feed status including gaps. Reads from `FeedStatusManager`. Map as minimal API route group
- [ ] T043 [US5] Implement `BackfillEndpoints` in `src/AlgoTradeForge.HistoryLoader/Endpoints/BackfillEndpoints.cs` — `POST /api/v1/backfill` validates symbol exists in config, checks no backfill already running (409), queues backfill via `BackfillOrchestrator`, returns 202. Map as minimal API route group
- [ ] T044 [US5] Wire `IOptionsMonitor<HistoryLoaderOptions>` into all collector services — each service reads `CurrentValue.Assets` on every timer tick to pick up added/removed symbols without restart. Update `KlineCollectorService`, `FundingRateCollectorService`, `OiCollectorService`, `RatioCollectorService`, `HourlyCollectorService` to use `IOptionsMonitor` instead of `IOptions`
- [ ] T045 [US5] Register endpoints in `src/AlgoTradeForge.HistoryLoader/Program.cs`: map StatusEndpoints + BackfillEndpoints route groups, add `builder.Services.AddHealthChecks()`

**Checkpoint**: US5 complete — operator can query status, trigger backfills via HTTP, and hot-reload symbol configuration.

---

## Phase 8: User Story 6 — Top Trader Position Ratios and Liquidation Data (Priority: P6)

**Goal**: Collect top trader position ratios (1h) and forced liquidation events (4h) for advanced strategy signals.

**Independent Test**: Run one collection cycle, verify position ratio CSV has long_pct/short_pct/ratio columns. Verify liquidation CSV has side/price/qty/notional_usd fields.

### Implementation

- [ ] T046 [P] [US6] Add `FetchTopPositionRatioAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<FeedRecord>` to `BinanceFuturesClient` — `/futures/data/topLongShortPositionRatio`, returns `[long_pct, short_pct, ratio]`. Add test in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientPositionRatioTests.cs`
- [ ] T047 [P] [US6] Add `FetchLiquidationsAsync(symbol, from, to, ct) → IAsyncEnumerable<FeedRecord>` to `BinanceFuturesClient` — `/fapi/v1/allForceOrders`. Maps `side` to 1.0 (long liquidated) / -1.0 (short liquidated). Returns `[side, price, qty, notional_usd]`. Add test in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceFuturesClientLiquidationTests.cs`
- [ ] T048 [US6] Extend `SymbolCollector` — add `ls-ratio-top-positions` and `liquidations` feed dispatch. Also update `HourlyCollectorService` (from US4) to include `ls-ratio-top-positions` in its 1h cycle
- [ ] T049 [US6] Implement `LiquidationCollectorService` in `src/AlgoTradeForge.HistoryLoader/Collection/LiquidationCollectorService.cs` — `BackgroundService` with 4-hour `PeriodicTimer`. Register in `Program.cs`

**Checkpoint**: US6 complete — position ratios + liquidation data collected on schedule.

---

## Phase 9: User Story 7 — Spot Candle Ingestion Migration (Priority: P7)

**Goal**: Ingest Binance spot OHLCV klines in the new storage format, fully replacing the CandleIngestor.

**Independent Test**: Configure spot BTCUSDT, run backfill, verify candles written to `History/binance/BTCUSDT/candles/{YYYY-MM}_1m.csv` in new format.

### Implementation

- [ ] T050 [US7] Implement `BinanceSpotClient` in `src/AlgoTradeForge.HistoryLoader/Binance/BinanceSpotClient.cs` — `FetchKlinesAsync(symbol, interval, from, to, ct) → IAsyncEnumerable<KlineRecord>`. Uses `/api/v3/klines` with limit=1000. Same pagination/retry pattern as `BinanceFuturesClient`. Separate `SourceRateLimiter` for spot base URL. Add tests in `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceSpotClientTests.cs`
- [ ] T051 [US7] Extend `SymbolCollector` to route spot vs. futures based on `AssetCollectionConfig.Type` — spot symbols use `BinanceSpotClient` and only support `candles` feed (no derivatives feeds). Futures symbols use `BinanceFuturesClient` and support all feeds
- [ ] T052 [US7] Register `BinanceSpotClient` in `src/AlgoTradeForge.HistoryLoader/Program.cs`: `AddHttpClient<BinanceSpotClient>()`, `AddSingleton<SourceRateLimiter>` (keyed for spot URL). Update `BackfillOrchestrator` and `KlineCollectorService` to handle both spot and futures kline collection

**Checkpoint**: US7 complete — spot candle ingestion unified with futures collection. CandleIngestor can be retired.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Hardening, gap handling, and end-to-end validation

- [ ] T053 Implement gap detection and auto-backfill logic in `SymbolCollector` — during streaming, detect non-monotonic timestamp jumps exceeding expected interval → record as `DataGap` in `FeedStatus`. On each collection cycle, check for gaps within API history window → automatically backfill before extending forward. Add test for gap detection + backfill behavior
- [ ] T054 [P] Error handling hardening across all collector services — disk full detection (catch `IOException` on write, set feed health to Error, log critical), symbol delisting (HTTP 400 → mark symbol inactive, skip without blocking others), IP ban (HTTP 418 → stop all collection, alert). Update `SymbolCollector` and all `*CollectorService` classes
- [ ] T055 End-to-end validation: follow `quickstart.md` scenario — start HistoryLoader, trigger backfill for one futures + one spot symbol, verify CSV outputs, query status endpoint, run a backtest via WebApi that loads the collected data through `IFeedContext`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — **BLOCKS all user stories**
- **User Stories (Phase 3-9)**: All depend on Foundational phase completion
  - US1 (P1): No inter-story dependencies
  - US2 (P2): No inter-story dependencies (uses same `SymbolCollector` class but different methods)
  - US3 (P3): Implementation independent; **integration testing** requires US1 data on disk
  - US4 (P4): No dependencies on other stories (extends `SymbolCollector` + `BinanceFuturesClient`)
  - US5 (P5): Depends on US1 or US2 existing for meaningful status data; implementation independent
  - US6 (P6): No dependencies; extends same patterns as US2/US4
  - US7 (P7): Depends on US1 for `SymbolCollector` framework; extends it with spot routing
- **Polish (Phase 10)**: Depends on all user stories being complete

### Within Each User Story

- Binance client methods before `SymbolCollector` extensions
- `SymbolCollector` extensions before collector services
- Collector services before DI registration

### Parallel Opportunities

Within phases, tasks marked [P] can execute in parallel. Across phases:
- US1 and US2 can proceed in parallel after Phase 2 (different client methods, different feeds)
- US3 can proceed in parallel with US1/US2 (different projects: Infrastructure vs. HistoryLoader)
- US4, US5, US6 can each start once their preceding dependencies complete

---

## Parallel Example: Phase 2 (Foundational)

```bash
# Group 1 (no dependencies):
T005: HistoryLoaderOptions config records
T006: BinanceIntervalMap (parallel — different file)
T009: KlineRecord + FeedRecord (parallel — different file)
T014: AssetDirectoryName helper (parallel — Infrastructure project)
T015: FeedMetadata.CandleConfig extension (parallel — Domain project)

# Group 2 (depends on Group 1 for rate limiter):
T007: WeightedRateLimiter
T008: SourceRateLimiter (parallel with T007 — wraps it but different file)

# Group 3 (depends on Group 1 for records/options):
T010: CandleCsvWriter
T011: FeedCsvWriter (parallel — different file)
T012: FeedSchemaManager (parallel — different file)
T013: FeedStatusManager (parallel — different file)
```

## Parallel Example: US1 + US3 Concurrent

```bash
# Developer A works on US1 (HistoryLoader project):
T016: BinanceFuturesClient.FetchKlinesAsync
T017: BinanceFuturesClient.FetchFundingRatesAsync (parallel)
T018: SymbolCollector kline + funding logic
T019-T022: Orchestration + services

# Developer B works on US3 (Infrastructure + Application projects):
T030: NewFormatBarLoader (parallel — different project)
T031: CsvFeedSeriesLoader (parallel — different project)
T032-T033: IFeedContextBuilder + FeedContextBuilder
T034-T036: HistoryRepository + BacktestPreparer + WebApi updates
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup → bootable host
2. Complete Phase 2: Foundational → all shared components
3. Complete Phase 3: US1 → kline + funding rate backfill works
4. **STOP and VALIDATE**: Backfill BTCUSDT_fut, verify CSV files
5. This delivers: a working data collection service for the two most critical feeds

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. US1 → Kline + funding backfill (MVP)
3. US2 → Tier 2 forward collection (time-critical — deploy ASAP to start capturing OI/ratios)
4. US3 → Backtest engine can read new data (closes collection → strategy loop)
5. US4 → Mark price + extended klines (enriches backtesting signals)
6. US5 → Monitoring + HTTP API (operational readiness)
7. US6 → Position ratios + liquidations (additional signals)
8. US7 → Spot migration (CandleIngestor retirement)

### Critical Path

**US2 is time-critical** — every day without Tier 2 forward collection is data permanently lost (30-day API window). Prioritize deploying US1 + US2 together as the first production deployment.

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- [Story] label maps each task to its user story for traceability
- Each user story is independently testable after Phase 2
- All Binance client tests use `FakeHandler : HttpMessageHandler` pattern (established in existing `BinanceAdapterTests`)
- All writer tests use temp directories with `IDisposable` cleanup (established in existing `CsvCandleWriterTests`)
- Constitution Principle III (Data Integrity): every writer test verifies append-only behavior, timestamp dedup, and correct encoding
