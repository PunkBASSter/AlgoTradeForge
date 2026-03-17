# Data Model: History Loader

**Branch**: `019-history-loader` | **Date**: 2026-03-13

## Existing Domain Types (Reused As-Is)

### FeedMetadata (`Domain/History/FeedMetadata.cs`)
Deserialization target for `feeds.json`. Existing type with one addition (T015).
```
FeedMetadata
├── Feeds: Dictionary<string, FeedDefinition>
│   ├── Interval: string          (e.g., "5m", "" for event-based)
│   ├── Columns: string[]         (e.g., ["oi", "oi_usd"])
│   └── AutoApply?: AutoApplyDefinition
│       ├── Type: string          (e.g., "FundingRate")
│       ├── RateColumn: string    (e.g., "rate")
│       └── SignConvention?: string
└── Candles?: CandleConfig                      (NEW — T015)
    ├── ScaleFactor: decimal      (e.g., 100 for 2-decimal assets)
    └── Intervals: string[]       (e.g., ["1m", "1d"])
```
The `CandleConfig` section in `feeds.json` describes the int64 encoding scale factor and which candle intervals are stored. Separate from the `Feeds` dictionary which describes double-valued auxiliary feeds.

### FeedSeries (`Domain/History/FeedSeries.cs`)
Column-major `double[][]` with `long[]` timestamps. No changes.

### DataFeedSchema (`Domain/History/DataFeedSchema.cs`)
Runtime descriptor: `(FeedKey, ColumnNames[], AutoApplyConfig?)`. No changes.

### Int64Bar (`Domain/History/Int64Bar.cs`)
`(TimestampMs, Open, High, Low, Close, Volume)` — all `long`. No changes.

---

## New Types — HistoryLoader Project

### Configuration Models

```
HistoryLoaderOptions (appsettings.json → "HistoryLoader" section)
├── DataRoot: string                    (default: "History")
├── MaxBackfillConcurrency: int         (default: 3)
├── Binance: BinanceOptions
│   ├── SpotBaseUrl: string             (default: "https://api.binance.com")
│   ├── FuturesBaseUrl: string          (default: "https://fapi.binance.com")
│   ├── MaxWeightPerMinute: int         (default: 2400)
│   ├── WeightBudgetPercent: int        (default: 40)
│   └── RequestDelayMs: int             (default: 50)
└── Assets: List<AssetCollectionConfig>
    ├── Symbol: string                  (e.g., "BTCUSDT")
    ├── Exchange: string                (e.g., "binance")
    ├── Type: string                    (e.g., "spot", "perpetual")
    ├── DecimalDigits: int              (for int64 candle encoding)
    ├── HistoryStart: DateOnly          (backfill start date)
    └── Feeds: List<FeedCollectionConfig>
        ├── Name: string               (e.g., "open-interest", "candles")
        ├── Interval: string            (e.g., "5m", "1m", "" for event-based)
        └── Enabled: bool               (default: true)
```

### Collection State Models

```
FeedStatus (persisted as {assetDir}/{feedName}/status.json)
├── FeedName: string
├── Interval: string
├── FirstTimestamp: long?               (epoch ms)
├── LastTimestamp: long?                (epoch ms)
├── LastRunUtc: DateTimeOffset?
├── RecordCount: long
├── Gaps: List<DataGap>
│   ├── FromMs: long                    (epoch ms)
│   └── ToMs: long                      (epoch ms)
└── Health: CollectionHealth            (enum: Healthy, Degraded, Error)
```

### Binance API Response Records

```
KlineRecord (internal, parsed from /fapi/v1/klines or /api/v3/klines)
├── TimestampMs: long                   (open time, epoch ms)
├── Open: decimal
├── High: decimal
├── Low: decimal
├── Close: decimal
├── Volume: decimal                     (base asset volume)
├── QuoteVolume: decimal                (quote asset volume — futures only)
├── TradeCount: int                     (number of trades — futures only)
├── TakerBuyVolume: decimal             (taker buy base volume — futures only)
└── TakerBuyQuoteVolume: decimal        (taker buy quote volume — futures only)

FeedRecord (internal, generic for all non-candle feeds)
├── TimestampMs: long                   (epoch ms)
└── Values: double[]                    (column values in definition order)
```

### API Request/Response Models

```
BackfillRequest (POST /api/v1/backfill)
├── Symbol: string                      (required)
├── Feeds: string[]?                    (optional — null = all configured feeds)
└── FromDate: DateOnly?                 (optional — null = use HistoryStart)

BackfillResponse
├── Symbol: string
├── FeedsQueued: string[]
└── Message: string

StatusResponse (GET /api/v1/status)
└── Symbols: List<SymbolStatus>
    ├── Symbol: string
    ├── Type: string
    └── Feeds: List<FeedStatusSummary>
        ├── Name: string
        ├── Interval: string
        ├── LastTimestamp: long?
        ├── GapCount: int
        └── Health: string

SymbolDetailResponse (GET /api/v1/status/{symbol})
├── Symbol: string
├── Type: string
└── Feeds: List<FeedStatus>             (full status including gaps)
```

---

## New Types — Application Layer

### IFeedContextBuilder (`Application/Abstractions/IFeedContextBuilder.cs`)

```
IFeedContextBuilder
└── Build(dataRoot: string, asset: Asset, from: DateOnly, to: DateOnly) → BacktestFeedContext?
```

Returns `null` if no `feeds.json` exists for the asset. Otherwise loads all feeds, builds schemas, registers with `BacktestFeedContext`, and returns it.

---

## New Types — Infrastructure Layer

### AssetDirectoryName (`Infrastructure/History/AssetDirectoryName.cs`)

```
AssetDirectoryName (static helper)
└── From(asset: Asset) → string
    CryptoPerpetualAsset → "{Name}_fut"
    FutureAsset → "{Name}_fut"
    CryptoAsset → "{Name}"
    EquityAsset → "{Name}"
```

### NewFormatBarLoader (`Infrastructure/History/NewFormatBarLoader.cs`)

Implements `IInt64BarLoader`. Reads new format:
- Path: `{dataRoot}/{exchange}/{assetDir}/candles/{YYYY-MM}_{interval}.csv`
- Header: `ts,o,h,l,c,vol`
- Timestamp: `long` epoch ms (parsed with `long.Parse()`)
- Values: `long` (pre-scaled by scale factor during ingestion)

### CsvFeedSeriesLoader (`Infrastructure/History/CsvFeedSeriesLoader.cs`)

Reads auxiliary feed CSVs into `FeedSeries`:
- Path: `{dataRoot}/{exchange}/{assetDir}/{feedName}/{YYYY-MM}[_{interval}].csv`
- Header: `ts,{col1},{col2},...`
- Timestamps: `long` epoch ms
- Values: `double` (parsed with `double.Parse(InvariantCulture)`)
- Returns: `FeedSeries(long[], double[][])` spanning `[from, to]`

---

## Storage Format Summary

### Directory Structure
```
{DataRoot}/
  {exchange}/
    {symbol}[_{type}]/
      feeds.json                        # Schema file
      candles/
        {YYYY-MM}_{interval}.csv       # Int64 OHLCV
      candle-ext/
        {YYYY-MM}_{interval}.csv       # Double extended kline fields
      funding-rate/
        {YYYY-MM}.csv                  # Double, no interval suffix
      open-interest/
        {YYYY-MM}_{interval}.csv       # Double
      ls-ratio-global/
        {YYYY-MM}_{interval}.csv       # Double
      ls-ratio-top-accounts/
        {YYYY-MM}_{interval}.csv       # Double
      ls-ratio-top-positions/
        {YYYY-MM}_{interval}.csv       # Double
      taker-volume/
        {YYYY-MM}_{interval}.csv       # Double
      liquidations/
        {YYYY-MM}.csv                  # Double, no interval suffix
      mark-price/
        {YYYY-MM}_{interval}.csv       # Double
```

### CSV Formats

**Candles** (int64): `ts,o,h,l,c,vol` — all values `long`
**All other feeds** (double): `ts,{columns...}` — ts is `long`, values are `double`

### feeds.json Schema
See spec section 3.4 for full example. Deserialized into existing `FeedMetadata` class.

---

## State Transitions

### Feed Collection Lifecycle

```
[Not Started] → (first collection cycle) → [Collecting]
[Collecting] → (network error / API error) → [Degraded]
[Degraded] → (successful recovery + gap backfill) → [Collecting]
[Collecting] → (disk full / unrecoverable error) → [Error]
[Error] → (manual intervention / restart) → [Collecting]
```

Health is derived, not set manually:
- **Healthy**: `LastRunUtc` within 2× expected interval, no gaps within API window
- **Degraded**: `LastRunUtc` exceeds 2× expected interval OR has backfillable gaps
- **Error**: Last collection attempt failed with unrecoverable error
