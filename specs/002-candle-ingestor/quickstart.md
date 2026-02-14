# Quickstart: Candle Data History Ingestion Service

**Branch**: `002-candle-ingestor` | **Date**: 2026-02-09

## Prerequisites

- .NET 10 SDK
- Git (on branch `002-candle-ingestor`)
- Internet access (for Binance API calls during integration testing)

## Build

```bash
dotnet build AlgoTradeForge.slnx
```

## Run the Ingestor

```bash
dotnet run --project src/AlgoTradeForge.CandleIngestor
```

The service will:
1. Run an initial ingestion on startup (if `RunOnStartup: true`)
2. Fetch candle history for all configured assets
3. Store integer CSV files under `Data/Candles/{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv`
4. Repeat every `ScheduleIntervalHours` (default: 6)

## Configuration

Edit `src/AlgoTradeForge.CandleIngestor/appsettings.json`:

```json
{
  "CandleIngestor": {
    "DataRoot": "Data/Candles",
    "ScheduleIntervalHours": 6,
    "RunOnStartup": true,
    "Adapters": {
      "Binance": {
        "Type": "Binance",
        "BaseUrl": "https://api.binance.com",
        "RateLimitPerMinute": 1200,
        "RequestDelayMs": 100
      }
    },
    "Assets": [
      {
        "Symbol": "BTCUSDT",
        "Exchange": "Binance",
        "SmallestInterval": "00:01:00",
        "DecimalDigits": 2,
        "HistoryStart": "2024-01-01"
      }
    ]
  }
}
```

## Run Tests

```bash
dotnet test AlgoTradeForge.slnx
```

## Verify Output

After a successful run, check the CSV output:

```bash
ls Data/Candles/Binance/BTCUSDT/2024/
# Expected: 2024-01.csv, 2024-02.csv, ..., 2024-12.csv

head -3 Data/Candles/Binance/BTCUSDT/2024/2024-01.csv
# Expected:
# Timestamp,Open,High,Low,Close,Volume
# 2024-01-01T00:00:00+00:00,4283215,4285100,4281000,4284300,153240000
# 2024-01-01T00:01:00+00:00,4284300,4286500,4283800,4285900,98760000
```

## Key Architecture

```
Write Path:  Exchange API → BinanceAdapter → RawCandle → IngestionOrchestrator → CsvCandleWriter → CSV files
Read Path:   CSV files → CandleLoader → TimeSeries<IntBar> → BacktestEngine
```

## Project Layout

| Project | Role |
|---------|------|
| `AlgoTradeForge.Domain` | `IntBar`, `RawCandle`, `IDataAdapter`, `Asset` (extended) |
| `AlgoTradeForge.Application` | `IngestionOrchestrator`, `CandleLoader` |
| `AlgoTradeForge.Infrastructure` | `BinanceAdapter`, `CsvCandleWriter` |
| `AlgoTradeForge.CandleIngestor` | Worker service host (`IngestionWorker`, `Program.cs`, config) |
