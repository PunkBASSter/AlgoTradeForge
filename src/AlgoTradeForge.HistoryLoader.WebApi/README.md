# AlgoTradeForge History Loader

ASP.NET Core Web API + BackgroundService host for collecting and backfilling historical market data from Binance (spot + USDT-M futures).

## Quick Start

```bash
# Build
dotnet build src/AlgoTradeForge.HistoryLoader.WebApi/

# Run
dotnet run --project src/AlgoTradeForge.HistoryLoader.WebApi/
```

The service starts on `https://localhost:64097` / `http://localhost:64098`.

## What It Does

On startup, the HistoryLoader launches **6 background services** that continuously collect data at different intervals:

| Service | Interval | Feeds Collected |
|---------|----------|-----------------|
| `KlineCollectorService` | Daily | `candles` (spot + futures) |
| `FundingRateCollectorService` | 8 hours | `funding-rate` |
| `OiCollectorService` | 5 minutes | `open-interest` |
| `RatioCollectorService` | 15 minutes | `ls-ratio-global`, `ls-ratio-top-accounts`, `taker-volume` |
| `HourlyCollectorService` | 1 hour | `mark-price`, `ls-ratio-top-positions` |
| `LiquidationCollectorService` | 4 hours | `liquidations` |

Each service iterates over all configured assets, checks which feeds are enabled, and collects data from the last known timestamp to now.

## Data Storage

Data is stored as flat CSV files under `%LOCALAPPDATA%/AlgoTradeForge/History/` (configurable via `HistoryLoader:DataRoot`).

```
History/
  binance/
    BTCUSDT_fut/               # Perpetual futures (symbol + "_fut")
      feeds.json               # Schema file — auto-generated
      candles/
        2024-01_1m.csv         # Monthly partitions with interval suffix
        2024-01_1d.csv
      funding-rate/
        2024-01.csv            # No interval suffix (fixed 8h schedule)
      open-interest/
        2024-01_5m.csv
      ls-ratio-global/
        2024-01_15m.csv
      ...
    BTCUSDT/                   # Spot (no type suffix)
      candles/
        2024-01_1m.csv
```

- **Candle data**: int64 values scaled by `10^DecimalDigits` (matching the `Int64Bar` pipeline)
- **Auxiliary feeds**: double precision CSV (funding rates, OI, ratios, etc.)
- **Timestamps**: Unix epoch milliseconds (int64)

## API Endpoints

### Status

```http
GET /api/v1/status/                    # All assets with feed health summary
GET /api/v1/status/{symbol}            # Single asset detail (e.g., BTCUSDT_fut)
POST /api/v1/status/circuit-breaker/reset  # Reset circuit breaker after IP ban
```

### Backfill

```http
POST /api/v1/backfill
Content-Type: application/json

{
  "Symbol": "BTCUSDT_fut",
  "Feeds": ["candles", "funding-rate"],  // optional — omit to backfill all
  "FromDate": "2023-01-01"               // optional — omit to use HistoryStart
}
```

Backfill runs asynchronously. Check progress via the status endpoint.

### Health Check

```http
GET /health
```

## Configuration

All configuration is in `appsettings.json` under the `HistoryLoader` section. The config supports hot-reload via `IOptionsMonitor` — changes to the asset list take effect without restart.

### Global Settings

```json
{
  "HistoryLoader": {
    "DataRoot": "C:/Data/History",
    "MaxBackfillConcurrency": 8,
    "CircuitBreakerCooldownMinutes": 15,
    "Binance": {
      "SpotBaseUrl": "https://api.binance.com",
      "FuturesBaseUrl": "https://fapi.binance.com",
      "MaxWeightPerMinute": 2400,
      "WeightBudgetPercent": 40,
      "RequestDelayMs": 50
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `DataRoot` | `%LOCALAPPDATA%/AlgoTradeForge/History` | Root directory for all CSV data |
| `MaxBackfillConcurrency` | 3 | Max concurrent backfill tasks per symbol |
| `CircuitBreakerCooldownMinutes` | 15 | Pause duration after HTTP 418 (IP ban) |
| `WeightBudgetPercent` | 40 | Percentage of Binance rate limit to use (2400 weight/min) |
| `RequestDelayMs` | 50 | Minimum delay between API requests |

### Asset Configuration

Each asset entry defines a symbol, its market type, and which feeds to collect:

```json
{
  "Symbol": "ETHUSDT",
  "Exchange": "binance",
  "Type": "perpetual",
  "DecimalDigits": 2,
  "HistoryStart": "2019-11-01",
  "Feeds": [
    { "Name": "candles", "Interval": "1m" },
    { "Name": "candles", "Interval": "1d" },
    { "Name": "funding-rate", "Interval": "" },
    { "Name": "open-interest", "Interval": "5m" },
    { "Name": "ls-ratio-global", "Interval": "15m" },
    { "Name": "ls-ratio-top-accounts", "Interval": "15m" },
    { "Name": "ls-ratio-top-positions", "Interval": "1h" },
    { "Name": "taker-volume", "Interval": "15m" },
    { "Name": "mark-price", "Interval": "1h" },
    { "Name": "liquidations", "Interval": "" }
  ]
}
```

| Field | Description |
|-------|-------------|
| `Symbol` | Binance trading pair (e.g., `BTCUSDT`) |
| `Exchange` | Exchange identifier (currently only `binance`) |
| `Type` | `spot`, `perpetual`, `future`, or `equity` |
| `DecimalDigits` | Price precision for int64 candle storage (scale factor = `10^n`) |
| `HistoryStart` | Earliest date for backfill (ISO 8601 date) |
| `Feeds[].Name` | Feed type (see Available Feeds below) |
| `Feeds[].Interval` | Collection resolution. Empty string for event-based feeds |
| `Feeds[].Enabled` | `true`/`false` (default: `true`). Disable without removing |
| `Feeds[].GapThresholdMultiplier` | Gap detection sensitivity (default: `2.0`) |

## Adding a New Asset

### 1. Add a spot pair

Add to `appsettings.json` → `HistoryLoader.Assets`:

```json
{
  "Symbol": "PEPEUSDT",
  "Exchange": "binance",
  "Type": "spot",
  "DecimalDigits": 8,
  "HistoryStart": "2023-05-01",
  "Feeds": [
    { "Name": "candles", "Interval": "1m" },
    { "Name": "candles", "Interval": "1d" }
  ]
}
```

### 2. Add a perpetual futures pair

```json
{
  "Symbol": "PEPEUSDT",
  "Exchange": "binance",
  "Type": "perpetual",
  "DecimalDigits": 8,
  "HistoryStart": "2023-05-01",
  "Feeds": [
    { "Name": "candles", "Interval": "1m" },
    { "Name": "candles", "Interval": "1d" },
    { "Name": "funding-rate", "Interval": "" },
    { "Name": "open-interest", "Interval": "5m" },
    { "Name": "ls-ratio-global", "Interval": "15m" },
    { "Name": "ls-ratio-top-accounts", "Interval": "15m" },
    { "Name": "ls-ratio-top-positions", "Interval": "1h" },
    { "Name": "taker-volume", "Interval": "15m" },
    { "Name": "mark-price", "Interval": "1h" },
    { "Name": "liquidations", "Interval": "" }
  ]
}
```

### 3. Trigger initial backfill

After adding the config, the background services will start collecting from `HistoryStart` on their next cycle. For immediate backfill:

```bash
curl -X POST http://localhost:64098/api/v1/backfill \
  -H "Content-Type: application/json" \
  -d '{"Symbol": "PEPEUSDT_fut"}'
```

The `Symbol` value in the backfill request uses the directory name convention: `{SYMBOL}` for spot, `{SYMBOL}_fut` for perpetual futures.

### Tips for choosing DecimalDigits

`DecimalDigits` determines the int64 scale factor for candle price storage. Set it to match the number of significant decimal places in the asset's price:

| Price Range | Example | DecimalDigits |
|-------------|---------|---------------|
| > $100 | BTC, ETH, BNB, SOL | 2 |
| $1 – $100 | LINK, DOT, AVAX | 3 |
| $0.01 – $1 | XRP, ADA | 4 |
| < $0.01 | DOGE, SHIB | 5–8 |

## Available Feeds

### Tier 1 — Full History (Backfillable)

| Feed Name | Interval | API Endpoint | Description |
|-----------|----------|-------------|-------------|
| `candles` | `1m`, `1d` | `/fapi/v1/klines` or `/api/v3/klines` | OHLCV price data |
| `mark-price` | `1h` | `/fapi/v1/markPriceKlines` | Mark price OHLC (futures only) |
| `funding-rate` | `""` (8h events) | `/fapi/v1/fundingRate` | Funding rate + mark price at settlement |

### Tier 2 — 30-Day Rolling Window

These feeds have only 30 days of API history. The loader builds deep history by collecting forward continuously.

| Feed Name | Interval | API Endpoint | Description |
|-----------|----------|-------------|-------------|
| `open-interest` | `5m` | `/futures/data/openInterestHist` | OI in contracts + USD value |
| `ls-ratio-global` | `15m` | `/futures/data/globalLongShortAccountRatio` | Global long/short account ratio |
| `ls-ratio-top-accounts` | `15m` | `/futures/data/topLongShortAccountRatio` | Top 20% trader account ratio |
| `ls-ratio-top-positions` | `1h` | `/futures/data/topLongShortPositionRatio` | Top trader position-weighted ratio |
| `taker-volume` | `15m` | `/futures/data/takeBuySellVol` | Aggressive buy/sell volume (USD) |

### Tier 3 — Very Limited History

| Feed Name | Interval | API Endpoint | Description |
|-----------|----------|-------------|-------------|
| `liquidations` | `""` (events) | `/fapi/v1/allForceOrders` | Forced liquidation events (7-day API window) |

## Extending with a New Exchange

The system uses a factory pattern for exchange-specific clients:

1. **Create a client** implementing the fetcher interfaces (`ICandleFetcher`, `IFeedFetcher`) in `AlgoTradeForge.HistoryLoader.Infrastructure`
2. **Register it** in `DependencyInjection.cs` using keyed DI:
   - Candle fetchers: keyed by `"{exchange}-{type}"` (e.g., `"bybit-futures"`)
   - Feed fetchers: keyed by `"{exchange}:{feedName}"` (e.g., `"bybit:funding-rate"`)
3. **Add assets** in `appsettings.json` with `"Exchange": "bybit"`

The `ICandleFetcherFactory` and `IFeedFetcherFactory` resolve the correct client based on the asset's exchange name.

## Extending with a New Feed Type

To add a new data feed (e.g., `orderbook-agg`):

1. **Add the feed name** constant to `FeedNames` in `AlgoTradeForge.HistoryLoader.Domain`
2. **Create a feed collector** extending `FeedCollectorBase` in `AlgoTradeForge.HistoryLoader.Application/Collection/Feeds/`
3. **Create a fetcher method** on the exchange client (e.g., `BinanceFuturesClient`)
4. **Register the collector** in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<IFeedCollector, OrderbookAggFeedCollector>();
   ```
5. **Add a background service** (or extend an existing one) if the new feed needs a different collection interval
6. **Add the feed** to asset configs in `appsettings.json`

## Architecture

```
AlgoTradeForge.HistoryLoader.WebApi        # ASP.NET Core host + endpoints + background services
  └─ AlgoTradeForge.HistoryLoader.Application  # Business logic, collectors, options
      └─ AlgoTradeForge.HistoryLoader.Domain   # Pure types (FeedNames, records, path conventions)
  └─ AlgoTradeForge.HistoryLoader.Infrastructure  # Binance HTTP clients, CSV writers, rate limiting
```

- **Circuit breaker**: Automatically pauses all collection on HTTP 418 (Binance IP ban). Reset via API or wait for cooldown.
- **Rate limiting**: `WeightedRateLimiter` tracks API weight budget. `WeightBudgetPercent` controls how much of Binance's 2400 weight/min limit to use.
- **Gap detection**: Monitors timestamp monotonicity. Non-monotonic jumps > `GapThresholdMultiplier × interval` are recorded in feed status.
- **Feed status**: Per-feed state files track first/last timestamps, record counts, gaps, and health. Persisted alongside CSV data.
- **Hot reload**: `IOptionsMonitor<HistoryLoaderOptions>` enables adding/removing assets without restarting the service.

## Monitoring

Check service health and collection progress:

```bash
# Overall status
curl http://localhost:64098/api/v1/status/

# Single asset
curl http://localhost:64098/api/v1/status/BTCUSDT_fut

# Health check (for load balancers / Docker)
curl http://localhost:64098/health
```

Logs are written to console and `logs/history-loader-{date}.log` with Serilog structured logging.
