# AlgoTradeForge Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-02-10

## Active Technologies
- C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature) (003-backtest-engine)
- In-memory `TimeSeries<Int64Bar>` loaded from CSV via existing `IInt64BarLoader`; no new storage (003-backtest-engine)
- TypeScript 5.x (strict mode, no `any`) / Node.js 20+ / Next.js 16 / Tailwind CSS 4 (CSS-first config) (008-trading-frontend)
- C# 14 / .NET 10 + ASP.NET Core (minimal APIs), System.Threading (Task.Run, Interlocked, CancellationTokenSource), Microsoft.Extensions.Caching.Distributed (IDistributedCache), existing Domain/Application/Infrastructure layers (009-long-running-ops)
- SQLite (existing, via SqliteRunRepository) for completed results; IDistributedCache (AddDistributedMemoryCache(), swappable to Redis) for in-progress state (009-long-running-ops)
- C# 14 / .NET 10 + ASP.NET Core (minimal APIs), `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json`, `Serilog`, `HttpClient` (019-history-loader)
- Flat monthly-partitioned CSV files + `feeds.json` schema files per asset directory (019-history-loader)
- C# 14 / .NET 10 + Existing AlgoTradeForge solution (Domain, Application, Infrastructure, WebApi). No new NuGet packages. (027-strategy-module-framework)
- N/A — all new types are in-memory domain objects. No persistence changes. (027-strategy-module-framework)

- C# 14 / .NET 10 + `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json` (Binance API parsing), `Serilog` (structured logging) (002-candle-ingestor)

## Project Structure

```text
src/
  AlgoTradeForge.Domain/
    Assets/            # Asset hierarchy: CryptoAsset, CryptoPerpetualAsset, EquityAsset, FutureAsset
    Collections/       # RingBuffer
    Engine/            # BacktestEngine, OrderValidator, BacktestFeedContext
    Events/            # IEventBus, backtest event types (market, order, signal, indicator)
    History/           # TimeSeries, Int64Bar, FeedSeries, DataFeedSchema, AutoApplyConfig
      Metadata/
    Indicators/        # ATR, DeltaZigZag
    Live/              # ILiveConnector, ILiveAccountManager
    Optimization/      # CartesianProductGenerator
      Attributes/      # [Optimizable], ParamUnit, [StrategyKey], [ModuleKey]
      Space/           # ParameterAxis, ResolvedAxis, ParameterCombination
    Reporting/         # PerformanceMetrics, MetricsCalculator
    Strategy/          # IInt64BarStrategy, IFeedContext, DataSubscription
      Modules/         # Pluggable strategy modules (filters, trade registry)
    Trading/           # Portfolio, Position, Order, Fill, ISettlementCalculator
  AlgoTradeForge.Application/
    Abstractions/      # ICommand, IQuery, IStrategyFactory
    Backtests/         # RunBacktestCommand, BacktestPreparer, BacktestSetup
    CandleIngestion/   # IInt64BarLoader, CandleStorageOptions
    Events/            # EventBus impl, sinks, post-run pipeline
    IO/                # IFileStorage
    Optimization/      # Optimization orchestration
    Progress/          # RunProgressCache, cancellation registry
    Repositories/      # Repository interfaces
    Strategies/        # Strategy listing queries
  AlgoTradeForge.Infrastructure/
    CandleIngestion/   # CsvInt64BarLoader
    Events/            # Event infrastructure
    History/           # CsvDataSource, HistoryRepository
    Persistence/       # SQLite repositories
    Plugins/           # PluginLoader
  AlgoTradeForge.WebApi/
  AlgoTradeForge.CandleIngestor/
tests/
  AlgoTradeForge.Domain.Tests/
  AlgoTradeForge.Application.Tests/
  AlgoTradeForge.Infrastructure.Tests/
```

## Commands

```bash
# Build
dotnet build AlgoTradeForge.slnx

# Test
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.Application.Tests/

# Build + test with private strategies
dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx
dotnet test ../AlgoTradeForge.Private/tests/AlgoTradeForge.Strategies.Private.Tests/
```

## Code Style

C# 14 / .NET 10: Follow standard conventions

### Int64 Money Convention

All monetary/price values in the Domain layer use `long` (Int64). When converting:

- **Domain internals**: Use `MoneyConvert.ToLong(decimal)` — NEVER raw `(long)` casts (which truncate instead of rounding)
- **Application/Infrastructure boundary**: Use `ScaleContext` (from `new ScaleContext(asset)` or `new ScaleContext(tickSize)`):
  - `scale.AmountToTicks(value)` — decimal amount → tick-denominated long
  - `scale.TicksToAmount(ticks)` / `scale.ToMarketPrice(ticks)` — long → decimal
  - `scale.FromMarketPrice(price)` — exchange price → tick-denominated long
- Raw `(long)` casts are ONLY acceptable for non-monetary values (timestamps, durations, indices)
- **Strategy parameters**: `ParamUnit.QuoteAsset` properties are scaled automatically by `ParameterScaler.ScaleQuoteAssetParams()` (backtest/live) or `OptimizationAxisResolver` (optimization). Do not manually scale.
- **Declaring `[Optimizable]` params**: Use `Unit = ParamUnit.QuoteAsset` for monetary `long` params (thresholds, ATR bounds); declare `Min`/`Max`/`Step` in human-readable units (dollars, not ticks). Use `ParamUnit.Raw` (default) for dimensionless params (periods, ratios).
- **Module sub-param scaling**: `ParameterScaler` recurses into `ModuleSelection` values to scale nested `QuoteAsset` sub-params. Both backtest/live (`ParameterScaler`) and optimization (`OptimizationAxisResolver`) paths handle module sub-param scaling.
- **User-facing templates/JSON**: Any code that exposes `ParamUnit.QuoteAsset` parameter defaults to the user (templates, API responses, UI forms) MUST convert tick-denominated `long` values to human-readable form. Use `StrategyTemplateBuilder.ConvertToHumanReadable()` or equivalent. Raw tick values in user-facing output will cause double-scaling when the user submits them back through `ParameterScaler`.
- **Parameter normalization (dedup)**: When a strategy has parameters that are conditionally irrelevant (e.g., `NumberOfLevels` has no effect when `Mode != FollowTrend`), the params class should implement `IParameterNormalizer` (`Domain.Optimization.Space`). The `Normalize()` method fixes irrelevant params to canonical values; the optimizer deduplicates identical normalized combinations automatically. Both brute-force and genetic paths apply normalization. The evaluate endpoint reports `UniqueCombinations` when a normalizer exists. `NormalizingEnumerable` (Application) wraps the lazy combination stream. Dedup stats are persisted as `DedupSkipped` on `OptimizationRunRecord`.
- **Indicator buffer memory (ring buffer)**: Indicators deriving from `IndicatorBase<T>` (`Int64IndicatorBase`, `DoubleIndicatorBase`) MUST call `ApplyBufferCapacity()` at end of constructor after populating `Buffers`. This bounds each `IndicatorBuffer<T>` to a `RingBuffer<T>`. `CapacityLimit`: `null` = auto `Max(MinimumHistory*2, 256)`, `0` = unbounded, `N` = fixed. `Count` reports total appended (not retained). `Set()` is a silent no-op on evicted indices; `Revise()` throws — if an indicator relocates pivots, capacity MUST cover its revision window. `SetCapacity()` MUST be called before any data is appended.

## Recent Changes
- 027-strategy-module-framework: Added C# 14 / .NET 10 + Existing AlgoTradeForge solution (Domain, Application, Infrastructure, WebApi). No new NuGet packages.
- 019-history-loader: Added C# 14 / .NET 10 + ASP.NET Core (minimal APIs), `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json`, `Serilog`, `HttpClient`
- 018-extra-data-feeds: Asset type hierarchy (CryptoAsset, EquityAsset, FutureAsset, CryptoPerpetualAsset), settlement system (ISettlementCalculator → CashAndCarry/Margin), aux data feeds (FeedSeries, IFeedContext, BacktestFeedContext, auto-apply), order validation (IOrderValidator), event bus (IEventBus/IEventBusReceiver)
- 009-long-running-ops: Added C# 14 / .NET 10 + ASP.NET Core (minimal APIs), System.Threading (Task.Run, Interlocked, CancellationTokenSource), IDistributedCache for progress tracking


<!-- MANUAL ADDITIONS START -->

## Private Strategies Repo

Sibling repo at `../AlgoTradeForge.Private/` contains private strategy plugins.

- **Source:** `../AlgoTradeForge.Private/src/AlgoTradeForge.Strategies.Private/`
- **Tests:** `../AlgoTradeForge.Private/tests/AlgoTradeForge.Strategies.Private.Tests/`
- **Full solution:** `../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx` (public + private)
- **Build private:** `dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx`
- **Test private:** `dotnet test ../AlgoTradeForge.Private/tests/AlgoTradeForge.Strategies.Private.Tests/`
- Post-build copies plugin DLL to `src/AlgoTradeForge.WebApi/plugins/`

When searching for strategy code, also search `../AlgoTradeForge.Private/` if not found locally.

## Domain Model Quick Reference

### Asset Hierarchy & Settlement
- `Asset` (abstract record) → `CryptoAsset`, `CryptoPerpetualAsset`, `EquityAsset`, `FutureAsset`
- Cash-settled (`ICashSettledAsset`): `CryptoAsset`, `EquityAsset` → `CashAndCarrySettlement` (full notional exchange)
- Margin-settled (`IMarginAsset`): `CryptoPerpetualAsset`, `FutureAsset` → `MarginSettlement` (realized PnL only)
- Settlement dispatch: `asset.GetSettlementCalculator()` returns singleton based on `SettlementMode`
- Validation: `MarginSettlement` checks `AvailableMargin(lastPrices)`; `CashAndCarrySettlement` checks `Cash` for buys, `AvailableMargin(lastPrices)` for shorts
- Auto-apply: `Asset.ComputeAutoApplyDelta()` handles funding rates, dividends, swap rates

### Auxiliary Data Feeds
- `FeedSeries` — column-major `double[][]` with `long[]` timestamps (zero-allocation reads via `GetRow`)
- `DataFeedSchema` — declares column names + optional `AutoApplyConfig` (type, rate column)
- `BacktestFeedContext` — engine-side `IFeedContext` impl; advances cursors per-bar, applies auto-apply cash flows
- Strategies implement `IFeedContextReceiver` to receive `IFeedContext` at init; query via `TryGetLatest(feedKey, out values)`

### Event Bus
- `IEventBus` — strategies implement `IEventBusReceiver` to receive at init; emit structured events
- Event types: `BarEvent`, `FillEvent`, `OrderSubmittedEvent`, `SignalEvent`, `IndicatorUpdateEvent`, etc.

<!-- MANUAL ADDITIONS END -->
