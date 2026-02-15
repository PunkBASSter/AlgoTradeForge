# AlgoTradeForge Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-02-10

## Active Technologies
- C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature) (003-backtest-engine)
- In-memory `TimeSeries<Int64Bar>` loaded from CSV via existing `IInt64BarLoader`; no new storage (003-backtest-engine)

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
- 003-backtest-engine: Added C# 14 / .NET 10 + Existing solution dependencies (no new NuGet packages required for this feature)

- 002-candle-ingestor: Added C# 14 / .NET 10 + `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json` (Binance API parsing), `Serilog` (structured logging)

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
