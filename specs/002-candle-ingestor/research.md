# Research: Candle Data History Ingestion Service

**Branch**: `002-candle-ingestor` | **Date**: 2026-02-09

## R-001: IntBar Field Widening (int → long)

**Decision**: Widen `IntBar` OHLC fields from `int` to `long`.

**Rationale**: The CSV writer produces `long` values via `(long)Math.Round(value * 10^DecimalDigits)`. For BTC at $100K with `DecimalDigits: 8`, the value exceeds `int.MaxValue` (2.1B). Using `long` future-proofs for high-precision assets and eliminates overflow risk.

**Alternatives considered**:
- Keep `int` with validation at config time → rejected because it constrains `DecimalDigits` and doesn't survive price growth
- Store `long` in CSV but downcast on load → rejected as inconsistent and fragile

**Impact**: Breaking change to `IntBar` record struct. All consumers (tests, metrics, engine) must update. Volume was already `long`, so only OHLC fields change.

## R-002: Backtest Engine Data Interface

**Decision**: Replace `IIntBarSource` (async stream) with `CandleLoader` returning `TimeSeries<IntBar>` directly.

**Rationale**: For file-based candle data at minute resolution, a month is ~44K rows (~2.6MB). Loading into memory upfront is fast (<1s) and simpler than maintaining an async streaming pipeline. The backtest engine iterates `TimeSeries<IntBar>` directly.

**Alternatives considered**:
- `CsvIntBarSource : IIntBarSource` bridge → rejected as unnecessary indirection; the engine benefits from random access (timestamp indexing) that streams don't provide
- Keep both async stream and in-memory loader → rejected per YAGNI; two read paths adds complexity

**Impact**: `IIntBarSource`, `IDataSource`, `IBarSourceRepository` retired. `BacktestEngine.RunAsync` signature changes to accept `TimeSeries<IntBar>`. `RunBacktestCommandHandler` refactored.

## R-003: Timestamp Type Consistency

**Decision**: Use `DateTimeOffset` (UTC, offset +00:00) throughout all candle ingestion code.

**Rationale**: Existing domain types (`TimeSeries<T>`, `BacktestOptions`, `HistoryDataQuery`) all use `DateTimeOffset`. The design doc used `DateTime` in `RawCandle`, but `DateTime` has the well-known `Kind` ambiguity in .NET. Consistency avoids bugs at type boundaries.

**Alternatives considered**:
- `DateTime` internally, convert at boundary → rejected because it invites `DateTimeKind.Unspecified` bugs

**Impact**: `RawCandle` and `IDataAdapter` use `DateTimeOffset`. CSV format remains ISO 8601 UTC strings.

## R-004: Unified Asset Model

**Decision**: Extend the existing `Asset` domain record with ingestion fields: `Exchange`, `DecimalDigits`, `SmallestInterval`, `HistoryStart`.

**Rationale**: One source of truth for asset metadata. The `IntBarMetadata.ToIntMultiplier` already exists for this purpose. Avoids parallel `AssetOptions` type that must stay in sync.

**Alternatives considered**:
- Separate `AssetOptions` config type → rejected because it creates a mapping burden and sync risk
- Composite `AssetProfile` aggregate → rejected as over-engineered for current needs

**Impact**: `Asset` record gains new optional fields with defaults. Existing code using `Asset.Equity()` or `Asset.Future()` factory methods is unaffected (new fields use defaults). `appsettings.json` asset config binds to an intermediate config record that maps to `Asset`.

## R-005: Broken Bar/IBarSource Reference Cleanup

**Decision**: Fix all broken `Bar` and `IBarSource` references in this feature.

**Rationale**: The codebase has broken references from an incomplete `OhlcvBar` → `IntBar` refactor. Since this feature already makes breaking changes to `IntBar` and the engine interface, cleaning up simultaneously avoids leaving the codebase in a non-compilable state.

**Files affected**:
- `IMetricsCalculator.cs` — `IReadOnlyList<Bar>` → `IReadOnlyList<IntBar>`
- `MetricsCalculator.cs` — all `Bar` references → `IntBar` (methods need rewrite for `long` OHLC)
- `BacktestEngineTests.cs` — `Bar`/`IBarSource` → `IntBar`/updated mocks
- `TestBars.cs` — `Bar` factory methods → `IntBar` factory methods (integer values, no decimals)
- `IBarSourceRepository.cs` — retire entirely (replaced by `CandleLoader`)
- `RunBacktestCommandHandler.cs` — remove `IBarSourceRepository` dependency

## R-006: Binance Klines API Specifics

**Decision**: Use Binance REST `/api/v3/klines` with pagination by `startTime`.

**Rationale**: Public endpoint, no auth required for historical klines. Returns up to 1000 candles per request. Weight: 2 per request. Rate limit: 1200 weight/min → 600 requests/min → 600K candles/min. A year of 1-min data (~525K) fetches in <1 minute.

**Key implementation notes**:
- Response is `JsonElement[]` where each element is a JSON array of 12 fields
- Prices are strings (not numbers) for decimal precision: `k[1].GetString()` → `decimal.Parse()`
- Timestamps are Unix milliseconds: `k[0].GetInt64()`
- Pagination: advance `startTime` to `lastCandle.Timestamp + interval` after each batch
- HTTP 429 → exponential backoff with jitter
- HTTP 418 (IP ban) → log critical, skip asset
- HTTP 5xx → retry up to 3 times

## R-007: CSV Writer Design

**Decision**: Append-mode `StreamWriter` with monthly partition routing and timestamp-based deduplication.

**Rationale**: Simple, no database needed. Monthly files at 1-min resolution are ~2.6MB — easily fits in memory for last-timestamp reads. Append-only with header detection (new file = write header, existing = append data only).

**Key design choices**:
- Flush after each pagination batch (~1000 rows), not per row
- Dedup by comparing incoming timestamp against last line of existing file
- Writer maintains open `StreamWriter` for current partition; switches on month boundary
- File format: `Timestamp,Open,High,Low,Close,Volume` header + data rows
- Timestamp format: ISO 8601 UTC (`2024-01-15T00:00:00+00:00`)

## R-008: Configuration Binding Strategy

**Decision**: Use `IOptions<CandleIngestorOptions>` with a dedicated config section. Asset list in config maps to `Asset` domain objects via a lightweight mapper.

**Rationale**: Standard .NET configuration pattern. The `CandleIngestorOptions` contains `DataRoot`, `ScheduleIntervalHours`, `RunOnStartup`, adapter configs, and an asset list. Assets in JSON config are intermediate DTOs that the orchestrator maps to domain `Asset` objects enriched with the new fields.

**Config hierarchy**:
```
CandleIngestor:
  DataRoot: "Data/Candles"
  ScheduleIntervalHours: 6
  RunOnStartup: true
  Adapters:
    Binance: { Type, BaseUrl, RateLimitPerMinute, RequestDelayMs }
  Assets:
    - { Symbol, Exchange, SmallestInterval, DecimalDigits, HistoryStart }
```

## R-009: Serilog Integration

**Decision**: Use Serilog with console + rolling file sinks for structured logging.

**Rationale**: Constitution requires structured JSON logs (Principle IV: Observability). Serilog is the de facto .NET structured logging library. Console sink for development, file sink for production diagnostics.

**Packages needed**: `Serilog.AspNetCore` (includes extensions for `IHostBuilder`), `Serilog.Sinks.Console`, `Serilog.Sinks.File`.
