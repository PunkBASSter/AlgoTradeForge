# HistoryLoader Launch Issues

Observed during a 1-hour monitoring session on 2026-03-15 (branch `019-history-loader`).
The HistoryLoader was started via `dotnet run` and monitored via Serilog file logs.

## Critical — Broken Feeds

### 1. funding-rate: Collects 0 Records Despite HTTP 200 (FIXED, NEEDS VERIFICATION)

- **Endpoint:** `GET /fapi/v1/fundingRate`
- **Symptom:** API returns HTTP 200 for every symbol, but the collector logs
  `"Collected 0 funding-rate records"` for all 10 symbols. No `funding-rate/`
  directory is created on disk.
- **Frequency:** Fires once at startup (8 h interval), so the issue repeats
  every 8 hours.
- **Impact:** Funding-rate data is completely missing. This feed has
  `autoApply` configuration in `feeds.json`, so backtests that depend on
  funding-rate cash-flow adjustments will produce incorrect results.
- **Root cause:** Binance does not populate the `markPrice` field in funding
  rate responses before Oct 31, 2023 08:00 UTC — it returns `""` (empty
  string) for ~4 years of history. `ParseFundingRateBatch` in
  `BinanceFuturesClient.FundingRate.cs:69-90` calls
  `BinanceJsonHelper.TryParseDouble(element, "markPrice", ...)` which returns
  `false` for empty strings, causing every record to be silently skipped. The
  first API batch returns 1000 records, all are dropped by the parser, so
  `FetchFundingRatesAsync` sees `batch.Length == 0` and `yield break`s without
  advancing the cursor — never reaching the Oct 2023+ data where `markPrice`
  is populated.
- **Fix:** Treat empty/missing `markPrice` as `double.NaN` instead of
  skipping the record. Only `fundingRate` is essential for backtests.
- **Status:** Root cause identified.

### 2. taker-volume: HTTP 404 for All Symbols

- **Endpoint:** `GET /futures/data/takeBuySellVol`
- **Symptom:** Every symbol receives HTTP 404. The endpoint appears to have
  been deprecated or relocated by Binance.
- **Frequency:** Repeats every 15-minute ratio-collector cycle (10 symbols ×
  4 cycles/hour = **40 wasted API calls per hour**).
- **Additional issue:** Because date discovery never succeeds, no
  `HistoryStart` is persisted. The collector falls back to the default start
  timestamp (`1602460800000` / Oct 2020) on every cycle instead of skipping.
- **Impact:** No taker buy/sell volume data is collected. Unnecessary API
  load.

### 3. liquidations: Endpoint Deprecated (HTTP 400)

- **Endpoint:** `GET /fapi/v1/allForceOrders`
- **Symptom:** Returns `400 "The endpoint has been out of maintenance"` for
  all symbols.
- **Frequency:** Fires once during the initial backfill pass, not repeated in
  streaming mode.
- **Impact:** No liquidation data is collected.

## Moderate — Behavioral Issues

### 4. Misleading 404 Warning Message

The taker-volume 404 produces:

```
[WRN] HTTP 404 for BTCUSDT, skipping (may be delisted or restricted)
```

This implies the *symbol* is delisted, but actually the *taker-volume
endpoint* is gone. The warning should include the feed name for clarity.

### 5. taker-volume Uses Ancient Start Date in Streaming

Even after the initial pass, the taker-volume collector requests data `from
1602460800000` (Oct 2020) because no `HistoryStart` was ever persisted (the
endpoint 404s before date discovery can complete). The collector should either
skip entirely after repeated 404s or use a recent fallback date.

### 6. Candle "0 Records" Double-Fetch

During the initial pass, each symbol's candle collection:
1. Fetches from an old start date → "Collected 0 candle records"
2. Retries from a later start date → still 0

This is the date-discovery binary search working correctly, but the log
output is confusing without context about what is happening.

## Noise — Logging Verbosity

HTTP client logging produces 6 lines per API call (Start processing, Sending,
Received headers, End processing). The 70-second initial backfill pass alone
generated **2171 log lines**. Consider demoting `HttpClient` logging from
`Information` to `Debug` in `appsettings.json`.

## Healthy Feeds (Confirmed Working)

| Feed                       | Interval | Streaming Behavior              |
| -------------------------- | -------- | ------------------------------- |
| open-interest/5m           | 5 min    | 1 record per symbol per cycle   |
| ls-ratio-global/15m        | 15 min   | 1 record per symbol per cycle   |
| ls-ratio-top-accounts/15m  | 15 min   | 1 record per symbol per cycle   |
| mark-price/1h              | 1 hour   | 1 record per symbol per cycle   |
| ls-ratio-top-positions/1h  | 1 hour   | 1 record per symbol per cycle   |
| candles/1d                 | daily    | Already up to date at launch    |

## Collector Schedule (Observed)

All schedules anchor to startup time, not clock-aligned:

| Collector                 | Interval | First Streaming Fire (after 20:30 startup) |
| ------------------------- | -------- | ------------------------------------------- |
| OiCollectorService        | 5 min    | 20:35                                       |
| RatioCollectorService     | 15 min   | 20:45                                       |
| HourlyCollectorService    | 1 hour   | 21:30                                       |
| KlineCollectorService     | daily    | Not observed (next day)                     |
| FundingRateCollectorService | 8 hours | Not observed (next cycle ~04:30)           |
| LiquidationCollectorService | 4 hours | Not observed (next cycle ~00:30)           |
