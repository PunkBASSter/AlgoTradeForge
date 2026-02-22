# Candle Ingestor — Candle Data Ingestion Service

## Design Document v1.0

**Project:** AlgoTradeForge / CandleIngestor  
**Runtime:** .NET 10 (`BackgroundService`)  
**Purpose:** Headless, scheduled ingestion of OHLCV candle data from exchange APIs into directory-partitioned integer CSV files, ready for backtest consumption as `TimeSeries<IntBar>`.

---

## 1. Motivation & Scope

The backtesting engine needs historical candle data available locally in a format that can be loaded directly into `TimeSeries<IntBar>` without float-to-int conversion at runtime. Today there is no automated pipeline — data must be fetched manually. The Candle Ingestor fills this gap as a lightweight, always-on service that:

- Runs on a schedule (daily or every N hours) as a .NET `BackgroundService`
- Fetches missing candle history from exchange REST APIs (no WebSocket — this is batch, not streaming)
- Stores candles as **integer-valued CSV** files partitioned by `{Exchange}/{Asset}/{Year}/{Month}.csv`
- Converts float OHLCV values to integers on write using a per-asset `DecimalDigits` multiplier (i.e., `price × 10^DecimalDigits`)
- Serves as the single source of truth for all backtest candle data

The design is intentionally simple — no database, no GUI, no real-time streaming. A headless cron-like background process with flat-file storage.

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                      Candle Ingestor Service                     │
│                                                                  │
│  ┌────────────┐    ┌──────────────┐    ┌──────────────────────┐  │
│  │  Scheduler  │───▶│ Ingestion    │───▶│  CSV Writer          │  │
│  │ (cron/timer)│    │ Orchestrator │    │  (int conversion +   │  │
│  └────────────┘    └──────┬───────┘    │   dir partitioning)  │  │
│                           │            └──────────────────────┘  │
│                    ┌──────┴───────┐                               │
│                    │   Adapter    │                               │
│                    │   Registry   │                               │
│                    └──────┬───────┘                               │
│              ┌────────────┼────────────┐                         │
│              ▼            ▼            ▼                         │
│       ┌──────────┐ ┌──────────┐ ┌──────────┐                   │
│       │ Binance  │ │  Bybit   │ │  Future  │                   │
│       │ Adapter  │ │ Adapter  │ │ Adapters │                   │
│       └──────────┘ └──────────┘ └──────────┘                   │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
            Data/Candles/{Exchange}/{Asset}/{Year}/
                              └── {YYYY-MM}.csv
```

### Key Components

| Component | Responsibility |
|---|---|
| **Scheduler** | Timer-based trigger. Configurable interval (e.g., every 6h). Runs on startup + repeats. |
| **Ingestion Orchestrator** | Iterates configured assets. For each: determines the last available candle timestamp from existing CSV, requests missing data from the adapter, passes raw candles to the writer. |
| **Adapter Registry** | Resolves the correct `IDataAdapter` implementation for a given exchange name from configuration. |
| **IDataAdapter** | Interface for exchange-specific logic: authentication, endpoint construction, rate limiting, response parsing. Returns `RawCandle[]` (float OHLCV). |
| **CSV Writer** | Converts `RawCandle` floats → integers via `× 10^DecimalDigits`, appends to the correct monthly partition file. Creates directories and files as needed. |

---

## 3. Configuration

### 3.1 Environment Variables (secrets only)

```env
# .env or host environment
BINANCE__APIKEY=abc123...
BINANCE__APISECRET=xyz789...
# Future adapters follow same convention:
# BYBIT__APIKEY=...
```

Loaded via `IConfiguration` with `__` as the hierarchy separator (standard .NET convention). Binance public market data doesn't require auth, but the config slot exists for future private endpoints or higher rate limits.

### 3.2 appsettings.json (everything else)

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
        "HistoryStart": "2020-01-01"
      },
      {
        "Symbol": "ETHUSDT",
        "Exchange": "Binance",
        "SmallestInterval": "00:01:00",
        "DecimalDigits": 2,
        "HistoryStart": "2020-01-01"
      }
    ]
  }
}
```

#### Asset Configuration Fields

| Field | Type | Description |
|---|---|---|
| `Symbol` | `string` | Exchange-native symbol (e.g., `BTCUSDT`) |
| `Exchange` | `string` | Key into `Adapters` dictionary |
| `SmallestInterval` | `TimeSpan` | Minimum candle period to fetch. `"00:01:00"` = 1 minute. |
| `DecimalDigits` | `int` | Multiplier exponent. `2` → multiply floats by `100`. BTC at `$67,432.15` → `6743215`. |
| `HistoryStart` | `DateOnly` | Earliest date to backfill from. On first run, fetches from this date to now. |

#### Adapter Configuration Fields

| Field | Type | Description |
|---|---|---|
| `Type` | `string` | Adapter implementation discriminator. Maps to a registered `IDataAdapter`. |
| `BaseUrl` | `string` | API base URL. Allows switching between production, testnet, or regional mirrors. |
| `RateLimitPerMinute` | `int` | Maximum requests per minute. Adapter enforces this internally. |
| `RequestDelayMs` | `int` | Minimum delay between consecutive requests. Safety net on top of rate limit. |

### 3.3 Configuration Binding

```csharp
public sealed record CandleIngestorOptions
{
    public string DataRoot { get; init; } = "Data/Candles";
    public int ScheduleIntervalHours { get; init; } = 6;
    public bool RunOnStartup { get; init; } = true;
    public Dictionary<string, AdapterOptions> Adapters { get; init; } = [];
    public List<AssetOptions> Assets { get; init; } = [];
}

public sealed record AdapterOptions
{
    public required string Type { get; init; }
    public required string BaseUrl { get; init; }
    public int RateLimitPerMinute { get; init; } = 1200;
    public int RequestDelayMs { get; init; } = 100;
}

public sealed record AssetOptions
{
    public required string Symbol { get; init; }
    public required string Exchange { get; init; }
    public TimeSpan SmallestInterval { get; init; } = TimeSpan.FromMinutes(1);
    public int DecimalDigits { get; init; } = 2;
    public DateOnly HistoryStart { get; init; } = new(2020, 1, 1);
}
```

---

## 4. Data Format & Storage

### 4.1 CSV Schema

Each CSV file contains integer-encoded OHLCVT candle data. **All price and volume values are pre-multiplied integers.** The file has a header row followed by candle rows sorted by timestamp ascending.

```
Timestamp,Open,High,Low,Close,Volume
2024-01-15T00:00:00Z,4283215,4285100,4281000,4284300,153240000
2024-01-15T00:01:00Z,4284300,4286500,4283800,4285900,98760000
```

| Column | Type | Description |
|---|---|---|
| `Timestamp` | `DateTime` (ISO 8601 UTC) | Candle open time |
| `Open` | `long` | Open price × 10^DecimalDigits |
| `High` | `long` | High price × 10^DecimalDigits |
| `Low` | `long` | Low price × 10^DecimalDigits |
| `Close` | `long` | Close price × 10^DecimalDigits |
| `Volume` | `long` | Volume × 10^DecimalDigits (same multiplier for simplicity) |

**Why integers?** The backtest engine operates on `TimeSeries<IntBar>`. Storing integers means the CSV loader does `long.Parse()` — no float parsing, no conversion step, no floating-point rounding artifacts. The multiplier is recorded in the asset config and carried through to the backtest context.

**Volume multiplier note:** Using the same `DecimalDigits` for volume as for price is a pragmatic simplification. For BTCUSDT with `DecimalDigits=2`, a volume of `1.5327 BTC` becomes `153`. If sub-satoshi volume precision matters for a specific asset, a separate `VolumeDecimalDigits` field can be added later. For most strategies operating at minute-bar granularity, this precision is more than sufficient.

### 4.2 Directory Layout

```
Data/
└── Candles/
    └── Binance/
        ├── BTCUSDT/
        │   ├── 2024/
        │   │   ├── 2024-01.csv    (44,640 rows max — 31d × 1440 min)
        │   │   ├── 2024-02.csv
        │   │   └── ...
        │   └── 2025/
        │       ├── 2025-01.csv
        │       └── ...
        └── ETHUSDT/
            └── ...
```

**Partition size: 1 month.** At 1-minute resolution, one month is ≤44,640 rows. With ~60 bytes per row, that's ~2.6 MB per file — easily fits in memory, fast to scan for the last timestamp, and naturally aligns with calendar boundaries.

**File naming:** `{YYYY-MM}.csv` within the `{Year}/` subdirectory. The year directory prevents flat listings from becoming unwieldy over multi-year histories.

### 4.3 Loading for Backtest

The backtest engine loads candle data by:

1. Resolving asset config → `DecimalDigits`, `Exchange`, `Symbol`
2. Globbing the partition files within the requested date range
3. Streaming CSV rows into `IntBar` structs via `long.Parse()` (no float conversion)
4. Assembling into `TimeSeries<IntBar>` with the multiplier stored as metadata

A thin `CandleLoader` utility (can live in a shared library) handles the path resolution and CSV parsing. The loader is read-only and has no dependency on CandleIngestor itself.

---

## 5. Data Adapter Layer

### 5.1 Interface

```csharp
public readonly record struct RawCandle(
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public interface IDataAdapter
{
    /// <summary>
    /// Fetch candles for the given symbol and interval, starting from
    /// <paramref name="from"/> up to <paramref name="to"/> (exclusive).
    /// Implementations handle pagination internally and yield results as they arrive.
    /// </summary>
    IAsyncEnumerable<RawCandle> FetchCandlesAsync(
        string symbol,
        TimeSpan interval,
        DateTime from,
        DateTime to,
        CancellationToken ct);
}
```

Adapters return `decimal` values (the exact floats from the exchange). The orchestrator is responsible for integer conversion using the asset's `DecimalDigits` before passing to the CSV writer. This keeps adapters pure data fetchers with no knowledge of the storage format.

`IAsyncEnumerable` allows the orchestrator to stream-write candles to disk as they arrive rather than buffering the entire history in memory — important for initial backfills spanning years of minute data.

### 5.2 Binance Adapter Implementation

Binance's `/api/v3/klines` endpoint returns up to 1000 candles per request. The adapter paginates by advancing `startTime` after each batch.

```csharp
public sealed class BinanceAdapter : IDataAdapter
{
    private readonly HttpClient _http;
    private readonly AdapterOptions _options;
    private readonly ILogger<BinanceAdapter> _logger;

    public async IAsyncEnumerable<RawCandle> FetchCandlesAsync(
        string symbol,
        TimeSpan interval,
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var intervalStr = MapInterval(interval); // "1m", "5m", "1h", etc.
        var cursor = from;

        while (cursor < to)
        {
            await EnforceRateLimitAsync(ct);

            var url = $"{_options.BaseUrl}/api/v3/klines"
                    + $"?symbol={symbol}"
                    + $"&interval={intervalStr}"
                    + $"&startTime={ToUnixMs(cursor)}"
                    + $"&endTime={ToUnixMs(to)}"
                    + $"&limit=1000";

            var response = await _http.GetFromJsonAsync<JsonElement[]>(url, ct);
            if (response is null || response.Length == 0)
                yield break;

            foreach (var k in response)
            {
                var candle = new RawCandle(
                    Timestamp: FromUnixMs(k[0].GetInt64()),
                    Open:      decimal.Parse(k[1].GetString()!),
                    High:      decimal.Parse(k[2].GetString()!),
                    Low:       decimal.Parse(k[3].GetString()!),
                    Close:     decimal.Parse(k[4].GetString()!),
                    Volume:    decimal.Parse(k[5].GetString()!));

                yield return candle;
            }

            // Advance cursor past the last received candle
            cursor = FromUnixMs(response[^1][0].GetInt64()) + interval;
        }
    }
}
```

**Rate limiting:** The adapter tracks request timestamps in a sliding window and delays when approaching `RateLimitPerMinute`. Additionally, `RequestDelayMs` is awaited between every request as a coarse safety valve. Binance's public klines endpoint has a weight of 2 per request, so 1200 weight/min ÷ 2 = 600 requests/min maximum. With 1000 candles per request, that's 600K candles/min — a full year of 1-minute data (~525K candles) in under a minute.

**Error handling:** HTTP 429 (rate limited) → exponential backoff with jitter. HTTP 418 (IP ban) → log critical, stop this asset, continue others. HTTP 5xx → retry up to 3 times. Network errors → retry with backoff. All errors are logged with structured context (symbol, time range, attempt number).

### 5.3 Adapter Registration

```csharp
// In Program.cs / DI setup
services.AddKeyedSingleton<IDataAdapter, BinanceAdapter>("Binance");
// Future:
// services.AddKeyedSingleton<IDataAdapter, BybitAdapter>("Bybit");

// Resolution in orchestrator:
var adapter = serviceProvider.GetRequiredKeyedService<IDataAdapter>(asset.Exchange);
```

The `Type` field in adapter config maps to the DI keyed service name. Adding a new exchange means implementing `IDataAdapter` and registering it — no changes to orchestrator or storage code.

---

## 6. Ingestion Orchestrator

The orchestrator is the core loop. On each scheduled run:

```
for each asset in config.Assets:
    1. Determine lastTimestamp from the most recent CSV partition file
       - If no files exist: lastTimestamp = asset.HistoryStart
       - Otherwise: parse the last row of the latest partition file
    2. Compute fetchFrom = lastTimestamp + interval
    3. Compute fetchTo = DateTime.UtcNow (rounded down to interval boundary)
    4. If fetchFrom >= fetchTo: skip (already up to date)
    5. Resolve adapter from registry
    6. Stream candles from adapter:
       - Convert each RawCandle to integer values
       - Route to the correct monthly partition CSV file
       - Append rows (create file with header if new)
    7. Log summary: asset, candles fetched, time range, duration
```

### 6.1 Gap Detection

On each run, the orchestrator checks for timestamp continuity: the first fetched candle's timestamp should equal `lastTimestamp + interval`. If there's a gap (e.g., exchange was down, or CandleIngestor was offline for days), the orchestrator logs a warning but proceeds normally — the gap will be filled by the adapter fetching from `lastTimestamp + interval`. Binance historical klines are available indefinitely, so gaps are always backfillable.

### 6.2 Concurrency

Assets are processed **sequentially** within a single adapter to respect rate limits. Different adapters (once multiple exist) could run in parallel since they have independent rate limits. For the MVP with only Binance, everything is sequential.

### 6.3 Integer Conversion

```csharp
static long ToInt(decimal value, int decimalDigits)
{
    var multiplier = (decimal)Math.Pow(10, decimalDigits);
    return (long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero);
}
```

The conversion happens at the boundary between adapter output and CSV writing. `RawCandle` (decimal) → `IntCandle` (long) → CSV row.

---

## 7. CSV Writer

### 7.1 Partition Routing

Given a candle timestamp, the writer resolves the target file:

```
{DataRoot}/{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv
```

The writer maintains an open `StreamWriter` for the current partition file and switches when the month boundary is crossed. This avoids reopening files for every row during sequential writes.

### 7.2 Write Behavior

- **New file:** Write header row, then data rows.
- **Existing file:** Open in append mode, write data rows only.
- **Duplicate prevention:** Before appending, the writer reads the last line of the existing file to get its timestamp. If the incoming candle timestamp ≤ existing last timestamp, it's skipped. This makes the write operation idempotent — re-running CandleIngestor after a crash or restart won't produce duplicates.
- **Flush policy:** Flush after each batch (adapter pagination boundary, typically 1000 rows). Not after every row — the OS page cache and SSD write buffering make per-row flushing wasteful for this use case.

### 7.3 Atomic Month Completion

When the orchestrator finishes writing all candles for a given month (i.e., it moves on to the next month), the completed month file is considered immutable. Future runs only ever append to the **current** (latest) month file. This means completed month files can be safely compressed, archived, or even backed up to S3/R2 without coordination.

---

## 8. Hosting & Lifecycle

### 8.1 Project Structure

```
AlgoTradeForge.sln
├── src/
│   ├── AlgoTradeForge.Domain/             # Shared domain: IntBar, TimeSeries<T>, etc.
│   ├── AlgoTradeForge.Application/        # Application logic, CandleLoader
│   ├── AlgoTradeForge.Infrastructure/     # Adapters (BinanceAdapter), CSV storage
│   ├── AlgoTradeForge.WebApi/             # Existing web host
│   └── AlgoTradeForge.CandleIngestor/     # ← New worker service project
│       ├── Program.cs
│       ├── appsettings.json
│       └── IngestionWorker.cs             # BackgroundService
└── tests/
    └── AlgoTradeForge.Domain.Tests/
```

**Layer responsibilities for candle ingestion:**

| Layer | Contents |
|---|---|
| **Domain** | `RawCandle`, `IntCandle`, `IDataAdapter` interface, `AssetOptions`/`AdapterOptions` records |
| **Application** | `IngestionOrchestrator`, `CandleLoader` (read-only CSV→IntBar, shared with Backtest) |
| **Infrastructure** | `BinanceAdapter`, `CsvCandleWriter`, rate limiting utilities |
| **CandleIngestor** | `Program.cs`, `IngestionWorker`, `appsettings.json`, DI wiring — thin hosting shell |

The CandleIngestor project is a minimal `dotnet new worker` host that references Application and Infrastructure. All reusable logic lives in the existing layers so the Backtest project can share `CandleLoader` via Application without depending on the worker.

### 8.2 Program.cs

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Bind configuration
builder.Services.Configure<CandleIngestorOptions>(
    builder.Configuration.GetSection("CandleIngestor"));

// Register adapters
builder.Services.AddHttpClient<BinanceAdapter>();
builder.Services.AddKeyedSingleton<IDataAdapter, BinanceAdapter>("Binance");

// Register core services
builder.Services.AddSingleton<CsvCandleWriter>();
builder.Services.AddSingleton<IngestionOrchestrator>();
builder.Services.AddHostedService<IngestionWorker>();

var host = builder.Build();
await host.RunAsync();
```

### 8.3 IngestionWorker

```csharp
public sealed class IngestionWorker(
    IngestionOrchestrator orchestrator,
    IOptions<CandleIngestorOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (options.Value.RunOnStartup)
        {
            logger.LogInformation("Running initial ingestion on startup");
            await orchestrator.RunAsync(ct);
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(options.Value.ScheduleIntervalHours));

        while (await timer.WaitForNextTickAsync(ct))
        {
            logger.LogInformation("Scheduled ingestion starting");
            await orchestrator.RunAsync(ct);
        }
    }
}
```

### 8.4 Deployment Options

The service is a plain `dotnet` console application that runs indefinitely. Deployment options (in order of simplicity):

1. **`dotnet run` / `dotnet AlgoTradeForge.CandleIngestor.dll`** — development / manual runs
2. **systemd service** — production on Linux (same Hetzner box as the Rust collector)
3. **Docker container** — if environment isolation is preferred
4. **Windows Service** — if running on Windows (`UseWindowsService()`)

For the initial setup, running alongside the existing Hetzner infrastructure as a systemd unit is the simplest path. The `Data/Candles/` directory can be a local path or a mounted volume.

### 8.5 AWS Deployment & Disk Persistence

If migrating to AWS, the service needs a POSIX filesystem for append-mode CSV writes. S3 is not suitable because objects are immutable blobs — you can't append a row to an existing CSV without re-uploading the entire file. Viable options:

| Service | How it works | Cost (est. 10GB) | Fit |
|---|---|---|---|
| **ECS Fargate + EFS** | Fargate runs the container; EFS provides a shared NFS mount that persists across task restarts. Mount `Data/Candles/` to an EFS access point. | ~$3/mo EFS + ~$5-10/mo Fargate (0.25 vCPU, 0.5GB) | **Best fit.** Zero ops, persistent filesystem, survives container restarts. EFS Infrequent Access tier drops storage to $0.025/GB/mo for older partitions. |
| **EC2 + EBS** | Smallest instance (t4g.nano/micro) with an EBS gp3 volume. | ~$3-5/mo EC2 + $0.80/mo EBS | Good if you want full control. EBS volume persists independently of the instance. Requires managing the EC2 lifecycle. |
| **ECS Fargate + S3 (hybrid)** | Write to local ephemeral storage during the run, then upload completed month files to S3 as immutable archives. The current month's CSV lives only in ephemeral storage and is rebuilt on restart by re-fetching from the exchange. | ~$0.23/mo S3 + Fargate cost | Cheapest storage, but the current month is lost on restart (acceptable since Binance history is always re-fetchable, adds ~1 min of backfill). |

**Recommendation:** ECS Fargate + EFS for production. The hybrid S3 approach is viable as a cost optimization if the re-fetch-on-restart trade-off is acceptable — since the service only runs every few hours and Binance historical klines are always available, losing the current month's partial file just means a slightly longer next run.

---

## 9. CandleLoader (Application Layer)

A thin, read-only utility in `AlgoTradeForge.Application` used by the backtest engine to load candle data. Both the CandleIngestor (for gap detection / last-timestamp reads) and the Backtest engine reference it via Application without circular dependencies.

```csharp
public static class CandleLoader
{
    /// <summary>
    /// Loads integer candle data for the given asset and date range.
    /// Returns a TimeSeries of IntBar ready for backtest consumption.
    /// </summary>
    public static TimeSeries<IntBar> Load(
        string dataRoot,
        string exchange,
        string symbol,
        int decimalDigits,
        DateOnly from,
        DateOnly to)
    {
        // 1. Enumerate monthly partition files within [from, to]
        // 2. For each file: stream-parse CSV rows into IntBar
        // 3. Filter rows outside the exact date range
        // 4. Return assembled TimeSeries with multiplier metadata
    }

    /// <summary>
    /// Reads the timestamp of the last row in the latest partition file.
    /// Used by the orchestrator to determine where to resume fetching.
    /// </summary>
    public static DateTime? GetLastTimestamp(
        string dataRoot, string exchange, string symbol);
}
```

---

## 10. Error Handling & Observability

### 10.1 Error Strategy

| Scenario | Behavior |
|---|---|
| API rate limit (HTTP 429) | Exponential backoff, retry indefinitely |
| API server error (5xx) | Retry up to 3×, then skip asset, continue others |
| IP ban (HTTP 418) | Log critical alert, skip asset |
| Network failure | Retry with backoff up to 5×, then skip asset |
| Malformed API response | Log error with raw response, skip batch, continue |
| Disk full | Let the exception propagate — service crashes, systemd restarts |
| Corrupt CSV (parse failure on last-timestamp read) | Log error, fall back to `HistoryStart` for that asset |

### 10.2 Logging

Structured logging via `ILogger` with Serilog sink to console + rolling file. Key log events:

- `IngestionStarted` — per-run: timestamp, asset count
- `AssetIngestionStarted` — per-asset: symbol, exchange, fetch range
- `BatchFetched` — per API call: candle count, time range, response time
- `AssetIngestionCompleted` — per-asset: total candles, duration, file path
- `IngestionCompleted` — per-run: total candles across all assets, duration
- `IngestionError` — any error with full context

### 10.3 Health Check

A simple file-based heartbeat: after each successful run, write the current UTC timestamp to `Data/candle-ingestor-heartbeat.txt`. External monitoring (UptimeRobot file check, or a simple script) can alert if the heartbeat is stale beyond `2 × ScheduleIntervalHours`.

---

## 11. Future Considerations

These are explicitly **out of scope for the MVP** but inform the design to avoid painting into a corner:

- **Additional adapters** (Bybit, Interactive Brokers, Kraken): the `IDataAdapter` interface and keyed DI registration make this straightforward. Each adapter manages its own auth, rate limiting, and response parsing.
- **Multiple timeframes**: the current design fetches only `SmallestInterval`. Higher timeframes (5m, 1h, 1d) can be aggregated from minute data by the backtest engine at load time, or pre-computed by a separate aggregation step writing to parallel partition trees.
- **S3/R2 sync**: completed month files are immutable and can be synced to cloud storage. A simple `aws s3 sync` cron job or a dedicated `IHostedService` that watches for completed months.
- **Parallel asset ingestion**: once multiple exchanges are configured, assets on different exchanges can be fetched concurrently. The orchestrator would group assets by exchange and run groups in parallel.
- **Integrity validation**: periodic full re-download of a random month to verify against stored data. Low priority given Binance's deterministic kline responses.
- **Resampled partition sizes**: if sub-minute data or tick data is added, the monthly partition may become too large. The partition strategy could be made configurable per-asset (daily, weekly, monthly).

---

## 12. MVP Implementation Checklist

1. [ ] Create `AlgoTradeForge.CandleIngestor` worker project in solution (`dotnet new worker`)
2. [ ] Define configuration POCOs in Domain, bind `appsettings.json`
3. [ ] Implement `IDataAdapter` interface (Domain) and `BinanceAdapter` (Infrastructure)
4. [ ] Implement `CsvCandleWriter` (Infrastructure) with directory partitioning and integer conversion
5. [ ] Implement `IngestionOrchestrator` (Application) — the main loop
6. [ ] Implement `IngestionWorker` (`BackgroundService` with `PeriodicTimer`)
7. [ ] Implement `CandleLoader` in Application layer (read-only CSV→IntBar)
8. [ ] Configure Serilog with console + file sinks
9. [ ] Write systemd unit file for Hetzner deployment
10. [ ] Initial backfill run: BTCUSDT + ETHUSDT from `HistoryStart` to present

**Estimated effort:** 3–4 focused days for the MVP (items 1–8), plus 1 day for deployment (9–10).
