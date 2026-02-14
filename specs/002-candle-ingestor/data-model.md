# Data Model: Candle Data History Ingestion Service

**Branch**: `002-candle-ingestor` | **Date**: 2026-02-09

## Domain Entities

### IntBar (modified)

Widened from `int` to `long` for OHLC fields.

```
IntBar (readonly record struct)
├── Open    : long       (was int)
├── High    : long       (was int)
├── Low     : long       (was int)
├── Close   : long       (was int)
└── Volume  : long       (unchanged)
```

**Validation**: None at struct level (value type, no invariants). Validation happens at conversion time.

### RawCandle (new)

Intermediate representation from exchange API response.

```
RawCandle (readonly record struct)
├── Timestamp : DateTimeOffset   (UTC)
├── Open      : decimal
├── High      : decimal
├── Low       : decimal
├── Close     : decimal
└── Volume    : decimal
```

**Lifecycle**: Created by `IDataAdapter`, consumed by orchestrator for integer conversion, then discarded. Not persisted.

### Asset (modified)

Extended with ingestion-related fields. New fields have defaults so existing factory methods remain valid.

```
Asset (sealed record)
├── Name              : string       (required, existing)
├── Type              : AssetType    (existing, default Equity)
├── Multiplier        : decimal      (existing, default 1m)
├── TickSize          : decimal      (existing, default 0.01m)
├── TickValue         : decimal      (computed, existing)
├── Currency          : string       (existing, default "USD")
├── MarginRequirement : decimal?     (existing)
├── Exchange          : string?      (NEW, default null — not all assets need ingestion)
├── DecimalDigits     : int          (NEW, default 2)
├── SmallestInterval  : TimeSpan     (NEW, default 1 minute)
└── HistoryStart      : DateOnly?    (NEW, default null — no backfill if null)
```

**Identity**: `Name` is the natural key (unique across the system).

**Relationships**:
- `Exchange` maps to a key in `CandleIngestorOptions.Adapters` dictionary
- `DecimalDigits` corresponds to `IntBarMetadata.ToIntMultiplier` via `10^DecimalDigits`

### SampleMetadata<T> / IntBarMetadata (unchanged)

```
SampleMetadata<T> (record)
├── ToIntMultiplier : int     (default 100)
└── Label           : string?

IntBarMetadata : SampleMetadata<IntBar> (sealed record)
└── Asset : Asset?
```

**Note**: `ToIntMultiplier` should match `10^Asset.DecimalDigits` when linked.

## Configuration Entities

### CandleIngestorOptions (new)

Top-level configuration bound from `appsettings.json`.

```
CandleIngestorOptions (sealed record)
├── DataRoot              : string                          (default "Data/Candles")
├── ScheduleIntervalHours : int                             (default 6)
├── RunOnStartup          : bool                            (default true)
├── Adapters              : Dictionary<string, AdapterOptions>  (default empty)
└── Assets                : List<IngestorAssetConfig>       (default empty)
```

### AdapterOptions (new)

Per-exchange connection configuration.

```
AdapterOptions (sealed record)
├── Type               : string   (required — maps to DI keyed service)
├── BaseUrl            : string   (required — API base URL)
├── RateLimitPerMinute : int      (default 1200)
└── RequestDelayMs     : int      (default 100)
```

### IngestorAssetConfig (new)

Asset configuration DTO from `appsettings.json`. Mapped to domain `Asset` by the orchestrator.

```
IngestorAssetConfig (sealed record)
├── Symbol           : string     (required — exchange-native symbol)
├── Exchange         : string     (required — key into Adapters)
├── SmallestInterval : TimeSpan   (default 00:01:00)
├── DecimalDigits    : int        (default 2)
└── HistoryStart     : DateOnly   (default 2020-01-01)
```

## Interfaces

### IDataAdapter (new, Domain)

```
IDataAdapter
└── FetchCandlesAsync(symbol, interval, from, to, ct) : IAsyncEnumerable<RawCandle>
```

**Contract**:
- Returns candles in ascending timestamp order
- Handles pagination internally
- Enforces rate limits internally
- Yields results as they arrive (streaming)

### IIntBarSource (retired)

Replaced by `CandleLoader` returning `TimeSeries<IntBar>` directly.

### IBarSourceRepository (retired)

No longer needed — `CandleLoader` is the data access path.

### IDataSource (retained, updated)

```
IDataSource
└── GetData(query: HistoryDataQuery) : TimeSeries<IntBar>
```

Potential implementation: `CsvDataSource` backed by `CandleLoader`.

## Storage Schema

### CSV Partition File

```
Path: {DataRoot}/{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv

Header: Timestamp,Open,High,Low,Close,Volume
Row:    2024-01-15T00:00:00+00:00,6743215,6745100,6741000,6744300,153240000

Column Types:
├── Timestamp : DateTimeOffset (ISO 8601 with UTC offset)
├── Open      : long (price × 10^DecimalDigits)
├── High      : long (price × 10^DecimalDigits)
├── Low       : long (price × 10^DecimalDigits)
├── Close     : long (price × 10^DecimalDigits)
└── Volume    : long (volume × 10^DecimalDigits)
```

**Partition size**: 1 month, max ~44,640 rows at 1-min resolution (~2.6 MB).

**Immutability**: Completed months are never modified. Only the current (latest) month is appended to.

## Entity Relationships

```
CandleIngestorOptions
 ├── has many → AdapterOptions (keyed by exchange name)
 └── has many → IngestorAssetConfig
                  └── maps to → Asset (domain, enriched with ingestion fields)
                                  └── referenced by → IntBarMetadata

IDataAdapter (one per exchange)
 └── produces → RawCandle (streaming)
                  └── converted to → IntBar (via DecimalDigits multiplier)
                                       └── written to → CSV Partition File
                                       └── loaded into → TimeSeries<IntBar> (by CandleLoader)
```
