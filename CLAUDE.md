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
- C# 14 / .NET 10 (backend), TypeScript 5.x strict (frontend) + ASP.NET Core minimal APIs, System.Threading, TanStack Query, Next.js 16, CodeMirror 6 (028-dss-optimization-split)
- SQLite via SqliteRunRepository (existing) + new tables for groups (028-dss-optimization-split)

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
tests/
  AlgoTradeForge.Domain.Tests/
  AlgoTradeForge.Application.Tests/
  AlgoTradeForge.Infrastructure.Tests/
```

## Commands

**CRITICAL: Only ONE dotnet process at a time. Never run build, test, or run commands in parallel. Wait for each to finish before starting the next.**

```bash
# Build
dotnet build AlgoTradeForge.slnx

# Test (run sequentially, never in parallel)
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

### Comment Convention (Constitution v1.8.4)

- **Prefer no XML or inline comments.** Code is self-documenting through clear naming, small methods, and explicit types. Don't write a comment that just restates the identifier or the signature.
- **Allowed when** (and only when) the code involves: a non-obvious algorithm or formula, a known pitfall / workaround / counterintuitive behavior, or a `TODO`/`HACK` with justification.
- **When you do write one, keep it terse.** Prefer a single line. Several lines are acceptable only when the documented behavior is genuinely non-obvious or non-conventional. No multi-paragraph essays, no signature restatement, no English paraphrase of the identifier (`<summary>The user identifier.</summary>` on `UserId` is forbidden). Shortest text that conveys the non-obvious fact, nothing more.
- **Existing comments in validation stages and related domain types stay** — this convention applies to writing new comments and editing existing ones, not to bulk-stripping documented code.

### File Organization (Constitution v1.9.0)

- **One type per file**, named after the type. `IFoo` lives in `IFoo.cs`; `FooImpl` lives in `FooImpl.cs`.
- **Exceptions:**
  - Single-line records / record structs declared next to the interface they accompany MAY share that file — e.g., `public readonly record struct TickResumeState(long LastAggId, long LastTsMs);` can sit beside `ITickFeedWriter`.
  - A non-generic + generic interface pair where one derives from the other (e.g., `IFoo` + `IFoo<T> : IFoo`) MAY share a file.
- **Extension methods** belong in their own file alongside the interface they extend (e.g., `IPartitionTailIndex.cs` + `PartitionTailIndexExtensions.cs`).

### Resource Release Convention (Constitution v1.9.1)

- **Prefer `using` over `try` / `finally`** whenever the `finally` is purely a release call. The modern form is the brace-less declaration:
  ```csharp
  using var stream = File.OpenRead(path);
  ```
  No parentheses, no `{ }` block — the resource is released when the enclosing scope exits.
- **`SemaphoreSlim` mutex use case** — acquire via `SemaphoreSlimExtensions.LockAsync` (`AlgoTradeForge.Application.Threading`):
  ```csharp
  using var _ = await _gate.LockAsync(ct);
  await DoWorkUnderLock(...);
  ```
  Do NOT write `await _gate.WaitAsync(ct); try { ... } finally { _gate.Release(); }` for new code. `RunProgressCache.AcquireRunKeyLockAsync` is the older per-key sibling; the generic extension is the right choice for a single static gate.
- `try` / `finally` remains correct when the cleanup branches on state, suppresses specific exceptions, or coordinates with anything beyond a single release call.

### Async I/O Convention (Constitution v1.8.3)

- **I/O-bound APIs MUST be async.** Any interface that fronts file storage, network HTTP, database access, an external service client, or a message broker MUST expose `Task` / `Task<T>` / `IAsyncEnumerable<T>` signatures with `CancellationToken ct = default` on every method.
- **No `Async` suffix on new or updated async methods.** Write `Task<bool> Exists(string key, CancellationToken ct = default)` — not `ExistsAsync(...)`. When you change the signature of an existing `Async`-suffixed method (or move it onto an async-only interface), drop the suffix as part of the change. Pre-existing `Async`-suffixed methods that you are not touching keep their names — this convention is applied incrementally to avoid bulk-rename churn.
- **No sync-over-async.** `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` are prohibited at production call sites. The only acceptable use is in narrowly-scoped test fixture cleanup or one-shot startup helpers that cannot be threaded async; such uses MUST carry an inline comment explaining why.
- **Reference impl**: `src/AlgoTradeForge.Application/IO/IFileStorage.cs` is the canonical example — fully async, no `Async` suffix, `CancellationToken` on every method, `IAsyncEnumerable<string>` for streaming reads, `IObjectWriteSession : IAsyncDisposable` with explicit `Commit()`.

## Recent Changes
- 028-dss-optimization-split: Added C# 14 / .NET 10 (backend), TypeScript 5.x strict (frontend) + ASP.NET Core minimal APIs, System.Threading, TanStack Query, Next.js 16, CodeMirror 6
- 028-dss-optimization-split: Added C# 14 / .NET 10 (backend), TypeScript 5.x strict (frontend) + ASP.NET Core minimal APIs, System.Threading, TanStack Query, Next.js 16, CodeMirror 6
- 027-strategy-module-framework: Added C# 14 / .NET 10 + Existing AlgoTradeForge solution (Domain, Application, Infrastructure, WebApi). No new NuGet packages.


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

## Performance Benchmarks

Performance regressions in engine, strategy, indicator, registry, or optimization hot paths MUST be caught with the BenchmarkDotNet harness at `benchmarks/AlgoTradeForge.Benchmarks/`. Use the `run-benchmarks` skill (`.claude/skills/run-benchmarks/SKILL.md`) or the `/benchmark` slash command — do not invent ad-hoc timing scripts.

- **Scenarios:** `BacktestBenchmarks.Backtest_5y_Hourly` (single-thread engine throughput) and `OptimizationBenchmarks.Optimization_1000Trials_Parallel` (mirrors `OptimizationTaskExecutor`'s loop shape).
- **Sample strategy:** `PrevBarBreakoutStrategy` (`src/AlgoTradeForge.Domain/Strategy/PrevBarBreakout/`, `[StrategyKey("PrevBarBreakout")]`) exercises `ModularStrategyBase` + `TradeRegistryModule` + `FixedNotionalModule` + `MaxHoldBarsModule` + ATR indicator end-to-end. ATR-based `MinVolatilityPct` filter is available for non-benchmark use.
- **Bundled data:** 5y BTCUSDT 1h CSVs (~2.4 MB) live under `benchmarks/AlgoTradeForge.Benchmarks/data/BTCUSDT_1h/` and are copied to the build output.
- **Workflow:** capture baseline on the parent commit, switch to the new commit, re-run, diff. Use the helper scripts (preferred over hand-diffing markdown):
  - `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 [-Filter '*Backtest_5y*'] [-Job dry|default] [-Label 'pre-fix']` — runs the harness and archives `*-report-brief.json` + markdown to `~/.algo-tradeforge/perf-history/<sha>[-dirty]-<utc>[-<label>]/` (machine-stamped via `metadata.json`; never committed).
  - `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest` — diffs Mean and Allocated, warns on machine/filter/job mismatches, prints a paste-ready PR summary.
  - Both **Mean** and **Allocated** matter; allocation regressions surface before wall-time ones.
- **JSON exporter wiring:** `BriefJsonConfig` (`benchmarks/AlgoTradeForge.Benchmarks/BriefJsonConfig.cs`) is applied via `[Config(typeof(BriefJsonConfig))]` on each `*Benchmarks` class — that's what produces the brief JSON the scripts consume.
- **Pre-flight:** never run benchmarks while another `dotnet` process is active on the machine — CPU contention destroys the measurement signal. `save-baseline.ps1` warns when it detects competing `dotnet` PIDs.
- **Shell:** this machine has no `pwsh` (PowerShell 7); always invoke `powershell.exe` (Windows PowerShell 5.1).

<!-- MANUAL ADDITIONS END -->
