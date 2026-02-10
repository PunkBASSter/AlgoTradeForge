# AlgoTradeForge.CandleIngestor

A .NET 10 worker service that ingests OHLCV candle data from configured sources and writes it into a partitioned CSV store.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Running as a console app

From the repository root:

```bash
dotnet run --project src/AlgoTradeForge.CandleIngestor
```

Or from the project directory:

```bash
cd src/AlgoTradeForge.CandleIngestor
dotnet run
```

Override settings via environment variables or command-line args:

```bash
dotnet run --project src/AlgoTradeForge.CandleIngestor -- --CandleIngestor:RunOnStartup=true --CandleIngestor:ScheduleIntervalHours=1
```

Set the environment to Development for verbose logging:

```bash
# PowerShell
$env:DOTNET_ENVIRONMENT="Development"
dotnet run --project src/AlgoTradeForge.CandleIngestor

# Bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/AlgoTradeForge.CandleIngestor
```

Stop the worker with `Ctrl+C` — it handles graceful shutdown via `CancellationToken`.

## Installing as a Windows Service

Publish a self-contained (or framework-dependent) binary:

```bash
dotnet publish src/AlgoTradeForge.CandleIngestor -c Release -o publish/candle-ingestor
```

Register the service with `sc.exe` (run as Administrator):

```powershell
sc.exe create AlgoTradeForgeCandleIngestor `
    binPath= "C:\full\path\to\publish\candle-ingestor\AlgoTradeForge.CandleIngestor.exe" `
    start= delayed-auto

sc.exe description AlgoTradeForgeCandleIngestor "AlgoTradeForge candle data ingestion worker"
sc.exe start AlgoTradeForgeCandleIngestor
```

Manage the service:

```powershell
sc.exe stop AlgoTradeForgeCandleIngestor
sc.exe delete AlgoTradeForgeCandleIngestor   # uninstall
```

> The `Microsoft.NET.Sdk.Worker` host automatically detects when it is running as a Windows Service — no code changes needed.

## Installing as a systemd service (Linux)

Publish:

```bash
dotnet publish src/AlgoTradeForge.CandleIngestor -c Release -o /opt/candle-ingestor
```

Create a unit file at `/etc/systemd/system/candle-ingestor.service`:

```ini
[Unit]
Description=AlgoTradeForge Candle Ingestor
After=network.target

[Service]
Type=notify
ExecStart=/opt/candle-ingestor/AlgoTradeForge.CandleIngestor
WorkingDirectory=/opt/candle-ingestor
Restart=on-failure
RestartSec=10
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable candle-ingestor
sudo systemctl start candle-ingestor
sudo journalctl -u candle-ingestor -f   # tail logs
```

## Configuration

All settings live in `appsettings.json` under the `CandleIngestor` section.

### Schedule

| Key | Default | Description |
|-----|---------|-------------|
| `RunOnStartup` | `true` | Run an ingestion cycle immediately on launch |
| `ScheduleIntervalHours` | `6` | Hours between subsequent ingestion cycles |
| `DataRoot` | `%LocalAppData%/AlgoTradeForge/Candles` | Root directory for partitioned output |

### Adapters

Each entry under `Adapters` registers a data source keyed by name.

**Binance** — pulls candles from the Binance REST API:

```json
"Binance": {
  "Type": "Binance",
  "BaseUrl": "https://api.binance.com",
  "RateLimitPerMinute": 1200,
  "RequestDelayMs": 100
}
```

**LocalCsv** — reads candles from local semicolon-delimited CSV files:

```json
"LocalCsv": {
  "Type": "LocalCsv",
  "BaseUrl": "./history_samples"
}
```

`BaseUrl` is the directory containing the source files. Files are matched by the glob pattern `*_{interval}_{symbol}@*.csv`, where interval is formatted as `hh-mm-ss` (e.g., `00-01-00` for 1-minute candles).

Expected CSV line format (no header, semicolon-delimited):

```
yyyyMMdd;HH:mm:ss;Open;High;Low;Close;Volume
20180101;00:00:00;13715.65000000;13715.65000000;13681.00000000;13707.92000000;2.84426600
```

### Assets

Each entry under `Assets` defines a symbol to ingest. The `Exchange` field must match an adapter key.

```json
{
  "Symbol": "BTCUSDT",
  "Exchange": "Binance",
  "SmallestInterval": "00:01:00",
  "DecimalDigits": 2,
  "HistoryStart": "2024-01-01"
}
```

To ingest from local CSV files instead, point the asset at the `LocalCsv` adapter:

```json
{
  "Symbol": "BTCUSDT",
  "Exchange": "LocalCsv",
  "SmallestInterval": "00:01:00",
  "DecimalDigits": 2,
  "HistoryStart": "2018-01-01"
}
```

## How it works

### Startup

`Program.cs` loads `appsettings.json`, binds the `CandleIngestor` section to `CandleIngestorOptions`, and iterates the `Adapters` config to register each one as a keyed `IDataAdapter` singleton in DI. The key is the config section name (e.g., `"Binance"`, `"LocalCsv"`), not the `Type` value. A single `CsvCandleWriter`, `IngestionOrchestrator`, and `IngestionWorker` (`BackgroundService`) are then registered, and the Generic Host starts.

### Scheduling

`IngestionWorker` calls `orchestrator.RunAsync()` immediately on startup if `RunOnStartup` is `true` (default), then enters a `PeriodicTimer` loop repeating every `ScheduleIntervalHours` (default 6). Non-cancellation exceptions are swallowed and logged — the worker stays alive for the next tick.

### Ingestion loop

`IngestionOrchestrator.RunAsync()` iterates the **`Assets` list** (not the adapters list) and processes each asset sequentially:

1. **Resolve adapter** — looks up `IDataAdapter` by the asset's `Exchange` key via `GetRequiredKeyedService`.
2. **Find resume point** — `CsvCandleWriter.GetLastTimestamp(exchange, symbol)` scans the output partition directory `{DataRoot}/{Exchange}/{Symbol}/` in reverse chronological order (newest year dir, newest month file, last data line). If found, `fetchFrom = lastTimestamp + SmallestInterval`. Otherwise falls back to `HistoryStart`.
3. **Short-circuit** — if `fetchFrom >= UtcNow`, logs "AssetUpToDate" and skips the asset.
4. **Stream candles** — `await foreach` over `adapter.FetchCandlesAsync(symbol, interval, fetchFrom, UtcNow, ct)`, writing each candle to the partition store via `CsvCandleWriter`. Flushes every 10,000 candles.
5. **Heartbeat** — after all assets are processed, writes a timestamp to `{DataRoot}/candle-ingestor-heartbeat.txt`.

### Deduplication

`CsvCandleWriter` has two dedup guards, both using `<=` comparison:

- **In-memory** — a `_lastWrittenTimestamp` field; any candle with a timestamp equal to or older than the last written one is silently dropped.
- **On partition switch** — when the writer opens a different month file, it reads the last timestamp from that file on disk and refreshes `_lastWrittenTimestamp`. This covers restarts mid-month.

### Resumption behavior

The orchestrator always resumes from `lastTimestamp + interval` — one step past the last stored candle. The writer's `<=` guard would also drop the last candle even if the adapter re-sent it. This means ingestion is **strictly append-only**: the last candle written before a shutdown is never overwritten or corrected. If the process was killed while a candle was still forming on the exchange (e.g., the 1-minute bar was only 30 seconds old), that candle is stored with whatever values the source returned at that moment and will not be updated on the next run.

### Multiple adapters for the same symbol

If both `LocalCsv` and `Binance` are configured for the same symbol and timeframe, they run as **completely independent pipelines**. The output partition path includes the exchange name:

```
{DataRoot}/Binance/BTCUSDT/2024/2024-01.csv
{DataRoot}/LocalCsv/BTCUSDT/2024/2024-01.csv
```

`GetLastTimestamp` scopes to `{exchange}/{symbol}`, so each adapter's resume point is tracked independently. They never merge into the same output files and never interfere with each other.

## Output structure

Candles are written as integer-encoded CSV files partitioned by month:

```
{DataRoot}/
  {Exchange}/
    {Symbol}/
      {Year}/
        {YYYY-MM}.csv
```

Example: `Candles/Binance/BTCUSDT/2024/2024-01.csv`

Each row: `Timestamp,Open,High,Low,Close,Volume` where OHLCV values are multiplied by `10^DecimalDigits` (e.g., `DecimalDigits=2` means `$67,432.15` is stored as `6743215`).

## Logs

- Console: structured one-liners via Serilog
- File: daily rolling JSON logs in `Logs/candle-ingestor-{Date}.log`
