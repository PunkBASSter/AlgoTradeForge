# Implementation Plan: History Loader — Binance Futures Vertical Slice

**Branch**: `019-history-loader` | **Date**: 2026-03-13 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/019-history-loader/spec.md`

## Summary

Build a Web API-hosted background service (`AlgoTradeForge.HistoryLoader`) that collects OHLCV candles and derivatives metrics from Binance Spot and USDT-M Futures APIs, stores them in a new monthly-partitioned CSV format with `feeds.json` schema files, and provides new Infrastructure loaders so the backtest engine can consume them as `Int64Bar` timeseries and `FeedSeries` auxiliary feeds. Replaces the existing `CandleIngestor` entirely.

## Technical Context

**Language/Version**: C# 14 / .NET 10
**Primary Dependencies**: ASP.NET Core (minimal APIs), `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json`, `Serilog`, `HttpClient`
**Storage**: Flat monthly-partitioned CSV files + `feeds.json` schema files per asset directory
**Testing**: xUnit + NSubstitute (existing stack)
**Target Platform**: Windows service / Linux daemon (ASP.NET Core Web API host)
**Project Type**: Web API + background workers within existing solution
**Performance Goals**: 50 symbols × 10 feed types, ≤40% of Binance rate limit budget (~51,860 weight/hour of 144,000/hour capacity), single-symbol Tier 1 backfill < 2 hours
**Constraints**: Single shared API key (≤60% total utilization), <25 MB/day forward collection storage, bounded parallel backfill (3-5 concurrent symbols)
**Scale/Scope**: 50 USDT-M perpetual futures + spot symbols, 10 distinct feed types, ~40 GB total with full M1 backfill

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Strategy-as-Code | N/A | Infrastructure feature — no strategy code affected |
| II. Test-First | ✅ PASS | Tests planned for all layers: adapter parsing, writer output, loader round-trip, rate limiter, status tracking |
| III. Data Integrity | ✅ PASS | Append-only CSVs (immutable after write), gap detection (completeness), epoch ms timestamps (precision), directory structure encodes source (provenance) |
| IV. Observability | ✅ PASS | Serilog structured logging (existing pattern), per-feed status.json files, HTTP health/status endpoints |
| V. Separation of Concerns | ✅ PASS | HistoryLoader = Background Job host + API; collection logic is idempotent and resumable; loader shared via Infrastructure. Uses BackgroundService + PeriodicTimer (CandleIngestor precedent) with FeedStatusManager providing its own persistence/checkpointing — satisfies durable jobs intent without Hangfire/Quartz |
| VI. Simplicity & YAGNI | ✅ PASS | Binance-specific client (no premature exchange abstraction), self-contained project (CandleIngestor precedent), reuses existing Domain types |

**Gate result: PASS** — no violations requiring justification.

## Project Structure

### Documentation (this feature)

```text
specs/019-history-loader/
├── plan.md              # This file
├── research.md          # Phase 0: resolved unknowns and design decisions
├── data-model.md        # Phase 1: entity definitions and state models
├── quickstart.md        # Phase 1: developer getting-started guide
├── contracts/           # Phase 1: HTTP API contracts
│   └── api.md           # Minimal API endpoint specifications
└── tasks.md             # Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── AlgoTradeForge.HistoryLoader/              # NEW — Web API host + background collection
│   ├── AlgoTradeForge.HistoryLoader.csproj    # Microsoft.NET.Sdk.Web, refs Application
│   ├── Program.cs                              # ASP.NET Core host, DI, endpoint mapping
│   ├── HistoryLoaderOptions.cs                 # Config records (assets, feeds, schedules)
│   ├── Endpoints/                              # Minimal API endpoints
│   │   ├── Models.cs                          # Request/response DTOs
│   │   ├── BackfillEndpoints.cs               # POST /api/v1/backfill
│   │   └── StatusEndpoints.cs                 # GET /api/v1/status, /health
│   ├── Binance/                                # Binance-specific API clients
│   │   ├── BinanceFuturesClient.cs            # /fapi/v1/* endpoints
│   │   ├── BinanceSpotClient.cs               # /api/v3/klines endpoint
│   │   └── BinanceIntervalMap.cs              # TimeSpan ↔ Binance interval string
│   ├── Collection/                             # Background collection services
│   │   ├── KlineCollectorService.cs           # Daily kline catch-up (candles + candle-ext)
│   │   ├── FundingRateCollectorService.cs     # 8h funding rate collection
│   │   ├── OiCollectorService.cs              # 5m open interest collection
│   │   ├── RatioCollectorService.cs           # 15m ratios + taker volume
│   │   ├── HourlyCollectorService.cs          # 1h mark price + position ratios
│   │   ├── LiquidationCollectorService.cs     # 4h liquidation events
│   │   ├── BackfillOrchestrator.cs            # On-demand + initial backfill coordination
│   │   └── SymbolCollector.cs                 # Per-symbol collection logic (all feeds)
│   ├── RateLimiting/                           # Thread-safe two-tier rate limiter
│   │   ├── WeightedRateLimiter.cs             # Global API weight budget (SemaphoreSlim-based)
│   │   └── SourceRateLimiter.cs               # Per-base-URL rate limiter
│   ├── Storage/                                # CSV writers for new format
│   │   ├── CandleCsvWriter.cs                 # Int64 OHLCV → {YYYY-MM}_{interval}.csv
│   │   ├── FeedCsvWriter.cs                   # Double aux feeds → {YYYY-MM}[_{interval}].csv
│   │   └── FeedSchemaManager.cs               # Reads/writes/updates feeds.json
│   ├── State/                                  # Collection state tracking
│   │   ├── FeedStatus.cs                      # Status model (last ts, gaps, health)
│   │   └── FeedStatusManager.cs               # Load/save per-feed status.json
│   └── appsettings.json                        # Config: data root, assets, Binance settings
│
├── AlgoTradeForge.Infrastructure/
│   ├── History/
│   │   ├── NewFormatBarLoader.cs              # NEW — IInt64BarLoader for new CSV format
│   │   ├── CsvFeedSeriesLoader.cs             # NEW — loads aux CSVs → FeedSeries
│   │   ├── FeedContextBuilder.cs              # NEW — builds BacktestFeedContext from feeds.json
│   │   ├── CsvDataSource.cs                   # DEPRECATED — dead code (registered but never injected)
│   │   └── HistoryRepository.cs               # MODIFIED — asset dir name mapping
│   └── CandleIngestion/
│       └── CsvInt64BarLoader.cs               # DEPRECATED — replaced by NewFormatBarLoader
│
├── AlgoTradeForge.Application/
│   ├── Abstractions/
│   │   └── IFeedContextBuilder.cs             # NEW — interface for feed context loading
│   └── Backtests/
│       └── BacktestPreparer.cs                # MODIFIED — inject IFeedContextBuilder, populate FeedContext
│
├── AlgoTradeForge.Domain/                      # MINIMAL CHANGES
│   └── History/
│       └── FeedMetadata.cs                    # EXISTING — already has FeedMetadata/FeedDefinition
│
└── AlgoTradeForge.WebApi/
    └── Program.cs                              # MODIFIED — register NewFormatBarLoader + FeedContextBuilder

tests/
├── AlgoTradeForge.HistoryLoader.Tests/        # NEW — HistoryLoader unit + integration tests
│   ├── Binance/                                # API client parsing tests (fake HTTP handler)
│   ├── Storage/                                # Writer round-trip tests
│   ├── RateLimiting/                           # Rate limiter behavior tests
│   └── State/                                  # Status manager tests
├── AlgoTradeForge.Infrastructure.Tests/
│   └── History/                                # NEW — NewFormatBarLoader, CsvFeedSeriesLoader tests
└── AlgoTradeForge.Application.Tests/
    └── Backtests/                              # MODIFIED — BacktestPreparer with feed context tests
```

**Structure Decision**: Single new host project (`AlgoTradeForge.HistoryLoader`) bundling all collection logic — matching the CandleIngestor self-contained pattern (constitution exemption from clean architecture layering). Shared read-side components (new format loader, feed context builder) live in Infrastructure because both WebApi and HistoryLoader consumers need them. The HistoryLoader project references only Application (for domain types via transitive reference).

## Key Design Decisions

### D1: Feed Data Flow — Write Path

```
BinanceFuturesClient.FetchKlinesAsync() → IAsyncEnumerable<KlineRecord>
  → CandleCsvWriter.WriteAsync(record)       → candles/{YYYY-MM}_{interval}.csv  (int64)
  → FeedCsvWriter.WriteAsync(record)          → candle-ext/{YYYY-MM}_{interval}.csv (double)

BinanceFuturesClient.FetchFundingRatesAsync() → IAsyncEnumerable<FeedRecord>
  → FeedCsvWriter.WriteAsync(record)          → funding-rate/{YYYY-MM}.csv (double)

BinanceFuturesClient.FetchOpenInterestAsync() → IAsyncEnumerable<FeedRecord>
  → FeedCsvWriter.WriteAsync(record)          → open-interest/{YYYY-MM}_{interval}.csv (double)
```

Each Binance client method returns a typed async stream. `KlineRecord` carries both OHLCV (for int64 candle writing) and extended fields (for double aux writing). All other feeds use `FeedRecord(long TimestampMs, double[] Values)` — the column semantics are defined by `feeds.json`, not by the record type.

### D2: Feed Data Flow — Read Path (Backtest)

```
BacktestPreparer.PrepareAsync()
  → IHistoryRepository.Load(subscription)
      → NewFormatBarLoader.Load(dataRoot, exchange, assetDir, from, to, interval)
          reads: History/{exchange}/{assetDir}/candles/{YYYY-MM}_{interval}.csv
          parses: ts(epoch ms),o,h,l,c,vol (all int64)
          → TimeSeries<Int64Bar>
  → IFeedContextBuilder.Build(dataRoot, asset, from, to)
      → FeedMetadataLoader: reads {assetDir}/feeds.json → FeedMetadata
      → For each FeedDefinition:
          → CsvFeedSeriesLoader.Load() → FeedSeries(long[], double[][])
          → Maps FeedDefinition → DataFeedSchema (+ AutoApplyConfig)
          → BacktestFeedContext.Register(feedKey, schema, series, asset)
      → BacktestFeedContext
  → BacktestSetup(asset, scale, options, strategy, series, feedContext)
```

### D3: Asset Directory Name Mapping

The new storage format uses `{symbol}[_{type}]` directories (e.g., `BTCUSDT_fut`, `BTCUSDT`). The `IInt64BarLoader` interface accepts a `symbol` string parameter. The mapping from `Asset` type to directory name lives in `HistoryRepository` (infrastructure concern, not domain):

- `CryptoPerpetualAsset` → `"{Name}_fut"`
- `FutureAsset` → `"{Name}_fut"`
- `CryptoAsset` / `EquityAsset` → `"{Name}"`

A static helper `AssetDirectoryName.From(Asset asset)` in Infrastructure provides this mapping.

### D4: Rate Limiter Architecture

Two-tier, thread-safe:

1. **`WeightedRateLimiter`** (global): Tracks total API weight consumed per minute using a `SemaphoreSlim`-coordinated sliding window. Every API call acquires weight before executing. Supports `AcquireAsync(int weight, CancellationToken)`.

2. **`SourceRateLimiter`** (per-base-URL): Wraps a `WeightedRateLimiter` scoped to a specific Binance base URL (`api.binance.com` vs `fapi.binance.com`). Each has independent weight pools but both feeds into the global limiter.

Bounded parallel backfill uses `SemaphoreSlim(maxConcurrency)` in `BackfillOrchestrator` — symbols acquire a concurrency slot, then individual API calls acquire rate limit weight.

### D5: Collection Scheduling

Each feed type runs as an independent `BackgroundService` with its own `PeriodicTimer`:

| Service | Timer | Feeds Collected |
|---------|-------|----------------|
| `KlineCollectorService` | Daily (backfill catch-up) | candles, candle-ext |
| `OiCollectorService` | 5 min | open-interest |
| `RatioCollectorService` | 15 min | ls-ratio-global, ls-ratio-top-accounts, taker-volume |
| `HourlyCollectorService` | 1 hr | ls-ratio-top-positions, mark-price |
| `LiquidationCollectorService` | 4 hr | liquidations |
| `FundingRateCollectorService` | 8 hr (or daily) | funding-rate |

Each service iterates all configured symbols on each tick, using the shared rate limiter. The backfill orchestrator runs separately (on-demand or at startup).

### D6: Reuse vs. New Code

| Component | Approach | Rationale |
|-----------|----------|-----------|
| `IDataAdapter` | Not reused | Returns `RawCandle` only — too narrow for aux feeds |
| `BinanceAdapter` | Superseded | New `BinanceFuturesClient` + `BinanceSpotClient` cover all endpoints |
| `RateLimiter` | Replaced | Not thread-safe; new `WeightedRateLimiter` supports concurrent access |
| `CsvCandleWriter` | Superseded | New `CandleCsvWriter` writes new format (epoch ms, `ts,o,h,l,c,vol`) |
| `IngestionState/Manager` | Pattern reused | New `FeedStatus/Manager` follows same atomic-write JSON pattern |
| `CsvInt64BarLoader` | Replaced | New `NewFormatBarLoader` reads new directory layout + header format |
| `FeedMetadata` | Reused as-is | Already matches `feeds.json` schema |
| `FeedSeries` | Reused as-is | Column-major double[][] — no changes needed |
| `DataFeedSchema` | Reused as-is | Runtime descriptor built from FeedDefinition |
| `BacktestFeedContext` | Reused as-is | `Register()` + `AdvanceTo()` already work |
| `MoneyConvert.ToLong()` | Reused | Int64 candle encoding |
| `BinanceAdapter.ToIntervalString()` | Logic reused | Extracted to shared `BinanceIntervalMap` |

## Complexity Tracking

No constitution violations to justify. The project follows the CandleIngestor precedent (self-contained host, exempt from clean architecture layering).
