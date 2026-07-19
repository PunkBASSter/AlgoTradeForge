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
| `KlineCollectorService` | Daily | `candles`, `candle-ext` (spot + futures) |
| `FundingRateCollectorService` | Cron (00:01, 08:01, 16:01 UTC) | `funding-rate` |
| `OiCollectorService` | 5 minutes | `open-interest` |
| `RatioCollectorService` | 15 minutes | `ls-ratio-global`, `ls-ratio-top-accounts`, `taker-volume` |
| `HourlyCollectorService` | 1 hour | `mark-price`, `ls-ratio-top-positions` |
| `LiquidationStreamService` | WebSocket stream | `liquidations` |

The first 5 services extend `ScheduledCollectorService` (periodic timer or cron). Each iterates over all configured assets, checks which feeds are enabled, and collects data from the last known timestamp to now.

`LiquidationStreamService` is a persistent WebSocket consumer that connects to Binance's `!forceOrder@arr` stream, filtering and writing events for configured symbols in real-time. It reconnects with exponential backoff (up to 10 attempts) and drops a stale connection after an idle timeout (no frames for 3 min) instead of hanging forever.

## Collection Configuration — Groups

Collection is configured declaratively via **groups**, stored durably (SQLite), editable via the API (`PUT /api/v1/groups/{name}`) or the web UI. **Groups are the source of truth.** The legacy `appsettings.json → HistoryLoader.Assets` list is a retired one-time bootstrap seed (empty by default) — see [Legacy asset config](#asset-configuration-legacy).

A group is **exchange-scoped** and declares each **feed once** (with its `collect` mode) plus the **instruments** it applies to. The cross-product (every symbol × every feed in the group) is what gets collected. `collect` is either:

- **`eager`** — actively collected/maintained (backfill for archivable feeds; live subscription for streams).
- **`on-demand`** — catalog-only; loaded when explicitly requested. Must be archive-replenishable.

### Feed-centric group

Declare a data feed once, set `eager` on it, and list the instruments it is collected for, within an exchange:

```json
PUT /api/v1/groups/binance-perp-liquidations
{
  "name": "binance-perp-liquidations",
  "enabled": true,
  "exchanges": ["binance"],
  "assets": { "symbols": ["BTC/USDT-PERP", "ETH/USDT-PERP", "SOL/USDT-PERP"], "historyStart": "2019-09" },
  "feeds": { "liquidations": { "collect": "eager" } }
}
```

Symbols are canonical (`BTC/USDT-PERP`, `BTC/USDT`), not Binance API symbols. All feeds in one group share one symbol list; for **different instrument sets per feed, use separate groups** — groups merge on expansion (`eager` beats `on-demand`).

### Default Binance seed

The default Binance collection is a feed-centric group set — one group per feed (family), all 10 majors — canonically in [`docs/binance-default-groups.json`](../../docs/binance-default-groups.json):

| Group | Feeds | collect |
|-------|-------|---------|
| `binance-perp-candles` | `candles` (1d/1h/1m) | eager |
| `binance-perp-open-interest` | `open-interest` | eager |
| `binance-perp-long-short-ratio` | `ls-ratio-global`, `ls-ratio-top-accounts`, `ls-ratio-top-positions` | eager |
| `binance-perp-taker-volume` | `taker-volume` | eager |
| `binance-perp-liquidations` | `liquidations` | eager |
| `binance-perp-funding-rate` | `funding-rate` | on-demand |
| `binance-perp-mark-price` | `mark-price` | on-demand |
| `binance-perp-ticks` | `ticks` | on-demand |
| `binance-spot-candles` | `candles` (1d/1h/1m) | eager |

Perp symbols: BTC/ETH/BNB/SOL/XRP/DOGE/ADA/AVAX/DOT/LINK `-USDT-PERP`; spot the same 10 without `-PERP`.

### Seeding a fresh install

An empty `Assets` seed means a fresh install starts with no groups. Apply the defaults with the seed script (idempotent — re-run to reset):

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/seed-binance-groups.ps1
# custom target: -BaseUrl http://host:5210
```

Or PUT them by hand from the JSON. Preview any group before committing with `POST /api/v1/groups/validate` (returns expansion tuple count + errors). Verify with `GET /api/v1/groups` and inspect the resolved plan via `GET /api/v1/desired-state`.

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
      candle-ext/
        2024-01_1m.csv         # Extended candle data (futures only)
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
      "FuturesWsBaseUrl": "wss://fstream.binance.com/market",
      "MaxWeightPerMinute": 2400,
      "WeightBudgetPercent": 40,
      "RequestDelayMs": 50
    },
    "Schedules": {
      "binance-funding-rate": {
        "Cron": "1 0,8,16 * * *",
        "TimeZone": "UTC"
      }
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
| `FuturesWsBaseUrl` | `wss://fstream.binance.com/market` | Futures WS base for liquidation/book-ticker streams. Must include `/market` — Binance decommissioned the legacy `/ws` path (2026-04-23); the old host still accepts connections but delivers no data |
| `Schedules` | `{}` | Cron schedules for services (see Cron Schedules below) |

### Asset Configuration (legacy)

> **Deprecated.** `HistoryLoader.Assets` is a one-time bootstrap seed, empty by default and retired in favour of [groups](#collection-configuration--groups). It is imported into groups only on first boot when no groups exist; once groups are present it is ignored. Prefer defining collection as groups (API/UI). This section documents the legacy format for reference.

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
| `Feeds[].HistoryStart` | Per-feed override for history start (auto-discovered if omitted) |
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
| `candle-ext` | `1m`, `1d` | (co-written with `candles`) | Extended candle data: quote vol, trade count, taker buy vol/quote (futures only) |
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

### Tier 3 — Streaming + Limited Backfill

| Feed Name | Interval | Source | Description |
|-----------|----------|--------|-------------|
| `liquidations` | `""` (events) | WebSocket `!forceOrder@arr` (live) / `/fapi/v1/allForceOrders` (backfill, 7-day window) | Forced liquidation events. Columns: `side` (1.0=long liq, -1.0=short liq), `price`, `qty`, `notional_usd` |

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
- **Write locking**: `WriteLockManager` prevents scheduled and backfill collections from writing to the same file simultaneously.
- **Gap detection**: Monitors timestamp monotonicity. Non-monotonic jumps > `GapThresholdMultiplier × interval` are recorded in feed status.
- **Feed status**: Per-feed state files track first/last timestamps, record counts, gaps, and health. Persisted alongside CSV data.
- **Hot reload**: `IOptionsMonitor<HistoryLoaderOptions>` enables adding/removing assets without restarting the service.
- **Date discovery**: When a feed's `HistoryStart` is too early (API returns HTTP 400 date-range error), `SymbolCollector` performs a binary search across months to find the earliest valid start date, then persists it to the history index via `IHistoryIndex.SetDiscoveredFirstMonth` and fires `CollectionChangeNotifier` to trigger a pipeline re-run.
- **Cron schedules**: Services can opt into cron-based scheduling (via `Cronos`) by overriding `ScheduleName`. Currently used by `FundingRateCollectorService` to align collection with Binance's 8-hour funding rate publication times.
- **Retry**: `BinanceRetryHelper` retries on network errors, HTTP 429, and 5xx with exponential backoff (2s, 4s, 8s; max 3 retries). HTTP 418 trips the circuit breaker. HTTP 400 date-range errors trigger date discovery. Other 4xx errors skip the symbol.

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
