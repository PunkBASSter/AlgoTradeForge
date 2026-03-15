# Research: History Loader — Binance Futures Vertical Slice

**Branch**: `019-history-loader` | **Date**: 2026-03-13

## R1: FeedSeries Loading Pipeline Gap

**Decision**: Create `IFeedContextBuilder` in Application + `FeedContextBuilder` in Infrastructure that reads `feeds.json`, loads all matching CSV files into `FeedSeries` objects, builds `DataFeedSchema` from `FeedDefinition`, and returns a populated `BacktestFeedContext`.

**Rationale**: The Domain types (`FeedMetadata`, `FeedSeries`, `DataFeedSchema`, `BacktestFeedContext`) already exist with full functionality. The only missing piece is the infrastructure loader that reads files and wires them together. A single `IFeedContextBuilder.Build(dataRoot, asset, from, to) → BacktestFeedContext?` interface provides the cleanest injection point into `BacktestPreparer`.

**Alternatives considered**:
- Separate `IFeedSeriesLoader` + `IFeedMetadataLoader` interfaces → rejected as over-abstracted for a single call site
- Extending `IInt64BarLoader` to also load feeds → rejected as SRP violation (bar loading ≠ feed loading)

## R2: New vs. Extended IInt64BarLoader

**Decision**: Create a new implementation `NewFormatBarLoader : IInt64BarLoader` in Infrastructure. Keep the interface unchanged. Deprecate `CsvInt64BarLoader`.

**Rationale**: The `IInt64BarLoader` interface (`Load` + `GetLastTimestamp`) is generic enough. The new implementation differs only in:
- Directory layout: `{exchange}/{assetDir}/candles/{YYYY-MM}_{interval}.csv` (no year subdir)
- CSV header: `ts,o,h,l,c,vol` (not `Timestamp,Open,...`)
- Timestamp format: epoch ms int64 (not ISO 8601 string)
- Interval in filename: `_{interval}` suffix
The interface doesn't need to change — only the DI registration swaps the implementation.

**Alternatives considered**:
- Extending `CsvInt64BarLoader` with format detection → rejected as violates SRP and adds complexity for a deprecated format
- New `IHistoryBarLoader` interface → rejected as unnecessary; existing interface works

## R3: Binance API Client Design

**Decision**: Two concrete client classes — `BinanceFuturesClient` (all `/fapi/v1/*` and `/futures/data/*` endpoints) and `BinanceSpotClient` (`/api/v3/klines` only). No shared interface initially.

**Rationale**: The Binance Futures API and Spot API have different base URLs, different response schemas, different rate limit pools, and different endpoint sets. A shared interface would be a premature abstraction. The clients are internal to the HistoryLoader project and not injected via DI externally — they're created and managed by the collection services.

**Alternatives considered**:
- Single `IBinanceClient` with all methods → rejected as spot has only klines, futures has 10+ endpoints
- Generic `IExchangeClient` abstraction → rejected as YAGNI; only Binance is in scope

## R4: Thread-Safe Rate Limiter Design

**Decision**: `WeightedRateLimiter` using `SemaphoreSlim` + sliding window of `(DateTimeOffset, int weight)` entries, protected by a `lock`. Each API call calls `AcquireAsync(weight, ct)` which waits if the rolling 1-minute weight sum would exceed the budget.

**Rationale**: The existing `RateLimiter` in CandleIngestor is not thread-safe (queue-based, no locking) — designed for single-threaded sequential processing. The History Loader needs concurrent access from multiple `BackgroundService` instances and parallel backfill tasks. A weight-based (not count-based) limiter correctly models Binance's API weight system where different endpoints cost different amounts.

**Alternatives considered**:
- `System.Threading.RateLimiting.TokenBucketRateLimiter` (.NET 7+) → viable but doesn't model weight-per-request natively; would need wrapper
- Per-service rate limiters without global coordination → rejected as different services share the same API key and rate limit pool

## R5: CSV Writer Design for Double-Valued Feeds

**Decision**: `FeedCsvWriter` — a general-purpose writer that takes `(long timestampMs, double[] values)` and appends to the correct monthly partition file. Column count is fixed per feed (from `FeedDefinition.Columns.Length`). Header row is `ts,{col1},{col2},...`.

**Rationale**: All auxiliary feeds share the same pattern: `ts` column (int64 epoch ms) + N double columns. The writer doesn't need to know the column semantics — it writes `timestamp,values[0],values[1],...` and the column names come from the feed definition for the header row. This avoids creating 10 separate writer classes.

**Alternatives considered**:
- Per-feed-type writer classes → rejected as code duplication; all have identical write logic
- Single unified writer for both int64 candles and double feeds → rejected as the encoding differs (int64 needs `MoneyConvert.ToLong()` + multiplier)

## R6: Asset Directory Name Convention

**Decision**: Static helper `AssetDirectoryName.From(Asset asset)` in Infrastructure maps asset types to directory names:
- `CryptoPerpetualAsset` / `FutureAsset` → `"{Name}_fut"`
- `CryptoAsset` / `EquityAsset` → `"{Name}"`

**Rationale**: The directory suffix (`_fut`) is a storage convention, not a domain concept. It must be consistent between the writer (HistoryLoader) and the reader (Infrastructure loaders). Centralizing it in a helper avoids duplication.

**Alternatives considered**:
- Adding `DirectoryName` property to `Asset` → rejected as storage concern in domain model
- Encoding the suffix in `Asset.Name` → rejected as it would affect all downstream code that uses the symbol name

## R7: Hot-Reload Configuration Pattern

**Decision**: Use `IOptionsMonitor<HistoryLoaderOptions>` to detect configuration changes. Each `BackgroundService` checks `CurrentValue` on each timer tick. New symbols start collecting on the next cycle; removed symbols stop.

**Rationale**: The .NET Options pattern with `IOptionsMonitor` provides built-in file-watching and change notification. This is simpler than a custom file watcher and integrates with the existing configuration pipeline. The "next cycle" latency is acceptable (5m-1h depending on feed type).

**Alternatives considered**:
- `IOptionsSnapshot<T>` → only works in scoped contexts (HTTP requests), not singleton background services
- Custom `FileSystemWatcher` on config file → more complex, same result
- HTTP endpoint to trigger reload → can be added later as enhancement

## R8: Backfill vs. Forward Collection Unification

**Decision**: Both backfill and forward collection use the same code path: `fetch(symbol, feed, fromTimestamp, toTimestamp) → write`. Backfill just uses a wider time range (contract inception → now) and runs with bounded parallelism. Forward collection uses a narrow range (lastTimestamp → now).

**Rationale**: The Binance API pagination works identically for backfill and forward collection. The only difference is the time range and concurrency. Unifying the code path means fewer bugs and a single set of tests for the fetch-parse-write pipeline.

**Alternatives considered**:
- Separate backfill and forward collection code → rejected as code duplication with identical API interaction
