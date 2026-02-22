# AlgoTradeForge Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-02-10

## Active Technologies
- C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature) (003-backtest-engine)
- In-memory `TimeSeries<Int64Bar>` loaded from CSV via existing `IInt64BarLoader`; no new storage (003-backtest-engine)
- TypeScript 5.x (strict mode, no `any`) / Node.js 20+ / Next.js 16 / Tailwind CSS 4 (CSS-first config) (008-trading-frontend)
- C# 14 / .NET 10 + ASP.NET Core (minimal APIs), System.Threading (Task.Run, ConcurrentDictionary, Interlocked, CancellationTokenSource), existing Domain/Application/Infrastructure layers (009-long-running-ops)
- SQLite (existing, via SqliteRunRepository) for completed results; ConcurrentDictionary (in-memory, volatile) for in-progress state (009-long-running-ops)

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

## Recent Changes
- 009-long-running-ops: Added C# 14 / .NET 10 + ASP.NET Core (minimal APIs), System.Threading (Task.Run, ConcurrentDictionary, Interlocked, CancellationTokenSource), existing Domain/Application/Infrastructure layers
- 008-trading-frontend: Updated to Next.js 16 / Tailwind CSS 4 (CSS-first `@theme` config, no tailwind.config.ts)
- 003-backtest-engine: Added C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature)


<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
