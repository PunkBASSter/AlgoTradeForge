# AlgoTradeForge Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-02-10

## Active Technologies
- C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature) (003-backtest-engine)
- In-memory `TimeSeries<Int64Bar>` loaded from CSV via existing `IInt64BarLoader`; no new storage (003-backtest-engine)
- TypeScript 5.x (strict mode, no `any`) / Node.js 20+ / Next.js 16 / Tailwind CSS 4 (CSS-first config) (008-trading-frontend)
- C# 14 / .NET 10 + ASP.NET Core (minimal APIs), System.Threading (Task.Run, Interlocked, CancellationTokenSource), Microsoft.Extensions.Caching.Distributed (IDistributedCache), existing Domain/Application/Infrastructure layers (009-long-running-ops)
- SQLite (existing, via SqliteRunRepository) for completed results; IDistributedCache (AddDistributedMemoryCache(), swappable to Redis) for in-progress state (009-long-running-ops)

- C# 14 / .NET 10 + `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json` (Binance API parsing), `Serilog` (structured logging) (002-candle-ingestor)

## Project Structure

```text
src/
tests/
```

## Commands

# Add commands for C# 14 / .NET 10

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

## Recent Changes
- 009-long-running-ops: Added C# 14 / .NET 10 + ASP.NET Core (minimal APIs), System.Threading (Task.Run, Interlocked, CancellationTokenSource), IDistributedCache for progress tracking
- 008-trading-frontend: Updated to Next.js 16 / Tailwind CSS 4 (CSS-first `@theme` config, no tailwind.config.ts)
- 003-backtest-engine: Added C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature)


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

<!-- MANUAL ADDITIONS END -->
