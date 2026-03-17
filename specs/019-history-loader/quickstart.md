# Quickstart: History Loader

**Branch**: `019-history-loader` | **Date**: 2026-03-13

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Binance API key (optional — public endpoints don't require authentication)

## Running Locally

From the repository root:

```bash
dotnet run --project src/AlgoTradeForge.HistoryLoader
```

The service starts an ASP.NET Core host with:
- Background collection services (one per feed type)
- HTTP endpoints for status and on-demand backfill
- Default port from `launchSettings.json`

## Configuration

All settings in `appsettings.json` under `"HistoryLoader"`:

```json
{
  "HistoryLoader": {
    "DataRoot": "History",
    "MaxBackfillConcurrency": 3,
    "Binance": {
      "SpotBaseUrl": "https://api.binance.com",
      "FuturesBaseUrl": "https://fapi.binance.com",
      "MaxWeightPerMinute": 2400,
      "WeightBudgetPercent": 40,
      "RequestDelayMs": 50
    },
    "Assets": [
      {
        "Symbol": "BTCUSDT",
        "Exchange": "binance",
        "Type": "perpetual",
        "DecimalDigits": 2,
        "HistoryStart": "2019-09-01",
        "Feeds": [
          { "Name": "candles", "Interval": "1m" },
          { "Name": "candles", "Interval": "1d" },
          { "Name": "funding-rate", "Interval": "" },
          { "Name": "open-interest", "Interval": "5m" },
          { "Name": "ls-ratio-global", "Interval": "15m" },
          { "Name": "taker-volume", "Interval": "15m" },
          { "Name": "mark-price", "Interval": "1h" }
        ]
      },
      {
        "Symbol": "BTCUSDT",
        "Exchange": "binance",
        "Type": "spot",
        "DecimalDigits": 2,
        "HistoryStart": "2020-01-01",
        "Feeds": [
          { "Name": "candles", "Interval": "1m" },
          { "Name": "candles", "Interval": "1d" }
        ]
      }
    ]
  }
}
```

## Key Operations

### Trigger a backfill

```bash
curl -X POST http://localhost:5000/api/v1/backfill \
  -H "Content-Type: application/json" \
  -d '{"symbol": "BTCUSDT_fut"}'
```

### Check collection status

```bash
curl http://localhost:5000/api/v1/status
curl http://localhost:5000/api/v1/status/BTCUSDT_fut
```

### Health check

```bash
curl http://localhost:5000/health
```

## Output Structure

After collection, data appears in the configured `DataRoot`:

```
History/
  binance/
    BTCUSDT_fut/
      feeds.json
      candles/
        2024-01_1m.csv
        2024-01_1d.csv
      funding-rate/
        2024-01.csv
      open-interest/
        2024-01_5m.csv
    BTCUSDT/
      feeds.json
      candles/
        2024-01_1m.csv
```

## Running Tests

```bash
dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/
dotnet test tests/AlgoTradeForge.Infrastructure.Tests/
```

## Using Collected Data in Backtests

Once the History Loader is running and the WebApi is configured with the same `DataRoot`, backtest runs automatically discover and load auxiliary feeds:

1. `BacktestPreparer` reads `feeds.json` for the asset
2. Available feeds are registered as `DataFeedSchema` entries
3. Strategies access feed data via `IFeedContext.TryGetLatest("open-interest", out values)`

No manual data preparation required — the backtest engine discovers feeds from `feeds.json` automatically.
