# TRD: Binance Futures Derivatives Metrics Collection

**Document Type**: Technical Requirements Document
**Scope**: CandleIngestor — Binance USDT-M Futures data collection pipeline
**Status**: Draft v1.0

---

## 1. Objectives

Extend the CandleIngestor to collect futures-specific derivatives metrics from Binance USDT-Margined Futures API, enabling:

- Backtesting of mid-frequency strategies (H1–D1 timeframes) that incorporate derivatives signals
- Building deep history for metrics that Binance only retains for 30 days
- Aggregated order book analytics without the storage/complexity cost of tick-level depth data
- A flat, integer-convertible storage format consistent with the existing OHLCV pipeline

---

## 2. Metrics Inventory

### 2.1 Tier 1 — Deep History Available (Backfillable)

These endpoints have no meaningful history depth limit. Full backfill from contract inception (2019-09 for BTCUSDT perp) is possible.

#### 2.1.1 Futures OHLCV Klines

- **Endpoint**: `GET /fapi/v1/klines`
- **Fields**: openTime, open, high, low, close, volume (base), closeTime, quoteAssetVolume, numberOfTrades, takerBuyBaseVolume, takerBuyQuoteVolume
- **Key difference from spot**: Includes `takerBuyBaseVolume` and `takerBuyQuoteVolume` natively, which are the aggressive buy-side components. The complement (total - takerBuy = takerSell) gives you buy/sell pressure decomposition directly from klines without a separate endpoint.
- **Resolution**: 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 6h, 8h, 12h, 1d, 3d, 1w, 1M
- **History depth**: Full — back to contract listing date
- **Pagination**: Max 1500 bars per request. Use `startTime`/`endTime` to paginate.
- **Collection priority**: **Immediate** — this is the core data. Collect at M1 and D1 resolution. H1 can be aggregated from M1 in post-processing.

#### 2.1.2 Mark Price Klines

- **Endpoint**: `GET /fapi/v1/markPriceKlines`
- **Fields**: openTime, open, high, low, close (all mark price), volume=0 (not applicable)
- **Purpose**: Mark price is used by Binance for liquidation calculations and PnL. It's an index-weighted price that smooths manipulation. The spread between mark price and last trade price (the "basis") is a signal: large positive basis = futures premium = bullish crowding; large negative = discount = bearish crowding.
- **Resolution**: Same intervals as klines
- **History depth**: Full
- **Collection priority**: **High** — collect at H1. Basis calculation (last - mark) is a key derivatives signal.

#### 2.1.3 Funding Rate History

- **Endpoint**: `GET /fapi/v1/fundingRate`
- **Fields**: symbol, fundingTime, fundingRate, markPrice
- **Format**: `fundingRate` is a decimal fraction (e.g., 0.0001 = 0.01%). Applied every 8 hours (00:00, 08:00, 16:00 UTC), though some pairs use 4h intervals.
- **History depth**: Full — back to contract inception
- **Pagination**: Max 1000 records per request. Paginate with `startTime`/`endTime`.
- **Collection priority**: **Immediate** — critical derivatives signal, trivial data volume (~3 records/day/symbol for 8h, ~1,095/year). Full backfill for 50 symbols takes minutes.

#### 2.1.4 Index Price Klines

- **Endpoint**: `GET /fapi/v1/indexPriceKlines`
- **Fields**: Same OHLC structure, no volume. Represents the composite spot index price that Binance uses to derive mark price.
- **History depth**: Full
- **Collection priority**: **Low** — can be derived from spot data. Collect only if cross-referencing basis calculations.

### 2.2 Tier 2 — 30-Day Rolling Window (Must Collect Forward)

These endpoints are on the `/futures/data/` path and share a hard **30-day history limit**. Data older than 30 days is permanently lost from the API. The CandleIngestor must poll these on a schedule and build its own deep history.

#### 2.2.1 Open Interest Statistics

- **Endpoint**: `GET /futures/data/openInterestHist`
- **Fields**: symbol, sumOpenInterest (contracts), sumOpenInterestValue (USD), timestamp
- **Resolution**: 5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d
- **History limit**: 30 days
- **Max per request**: 500 records
- **Collection priority**: **Critical** — this is the single most important derivatives metric for H1-D1 strategies. Rising OI + rising price = strong trend. Rising OI + falling price = bearish conviction/short buildup. Falling OI = liquidation/profit-taking.
- **Recommended collection interval**: **5m** — provides maximum flexibility for aggregation to any higher timeframe. At 288 records/day/symbol, storing 50 symbols = 14,400 records/day ≈ 1.2 MB/day uncompressed.

#### 2.2.2 Long/Short Account Ratio (Global)

- **Endpoint**: `GET /futures/data/globalLongShortAccountRatio`
- **Fields**: symbol, longAccount, shortAccount, longShortRatio, timestamp
- **Resolution**: 5m–1d
- **History limit**: 30 days
- **Collection priority**: **High** — measures retail crowding. Extreme readings (ratio > 3.0 or < 0.5) often precede reversals at H1-D1 scale. Collect at **15m**.

#### 2.2.3 Top Trader Long/Short Ratio (Accounts)

- **Endpoint**: `GET /futures/data/topLongShortAccountRatio`
- **Fields**: symbol, longAccount, shortAccount, longShortRatio, timestamp
- **Resolution**: 5m–1d
- **History limit**: 30 days
- **Collection priority**: **High** — same as global but filtered to top 20% of traders by volume. Divergence between top-trader ratio and global ratio is itself a signal (smart money vs. retail). Collect at **15m**.

#### 2.2.4 Top Trader Long/Short Ratio (Positions)

- **Endpoint**: `GET /futures/data/topLongShortPositionRatio`
- **Fields**: symbol, longPosition, shortPosition, longShortRatio, timestamp
- **Resolution**: 5m–1d
- **History limit**: 30 days
- **Collection priority**: **Medium** — similar to account ratio but weighted by position size. A top trader with a massive single position affects this more than accounts ratio. Collect at **1h**.

#### 2.2.5 Taker Buy/Sell Volume

- **Endpoint**: `GET /futures/data/takeBuySellVol`
- **Fields**: symbol, buyVol, sellVol, buySellRatio, timestamp
- **Resolution**: 5m–1d
- **History limit**: 30 days
- **Collection priority**: **High** — measures aggressive directional flow. Unlike the taker volume embedded in klines (which is base-asset denominated), this endpoint returns USD-denominated volumes, making cross-pair comparison meaningful. Collect at **15m**.

### 2.3 Tier 3 — Extremely Limited History

#### 2.3.1 Liquidation Orders

- **Endpoint**: `GET /fapi/v1/allForceOrders`
- **Fields**: symbol, price, origQty, executedQty, averagePrice, side, type, time, timeInForce
- **History limit**: **7 days**
- **Collection priority**: **Medium** — individual liquidation events. For H1-D1 strategies, aggregate into hourly buckets (total long liq USD, total short liq USD). Must poll at least daily; ideally every 6 hours to stay within the 7-day window.
- **Recommended polling**: Every **4 hours**, storing raw events. Aggregate to hourly/daily buckets in post-processing.

#### 2.3.2 Order Book Snapshot (Current Only)

- **Endpoint**: `GET /fapi/v1/depth`
- **Fields**: bids[[price, qty]], asks[[price, qty]], lastUpdateId, messageOutputTime, transactionTime
- **Depth levels**: 5, 10, 20, 50, 100, 500, 1000
- **History**: **None** — current state only. No historical snapshots from Binance.
- **Collection priority**: See Section 4 (Order Book Aggregation Strategy).

---

## 3. Storage Format

### 3.1 Design Principles

- **Flat CSV files** — same format family as existing CandleIngestor OHLCV pipeline, but with a different directory layout and timestamp format (see below)
- **Monthly partitions** — one file per symbol per month per metric type
- **Timeframe-qualified filenames** — files include the collection resolution in the name (`{YYYY-MM}_{interval}.csv`) when the metric is resolution-dependent. Event-based or fixed-schedule metrics (funding rate, liquidations) omit the suffix since they have no configurable resolution. Uses Binance interval notation: `1m`, `5m`, `15m`, `1h`, `1d`, etc.
- **Candle data: int64 arithmetic** — OHLCV candle values use int64 with a precision multiplier defined in `feeds.json`, consistent with existing `Int64Bar` pipeline
- **Non-candle feeds: double precision** — all auxiliary data feeds (funding rate, open interest, ratios, taker volume, liquidations, orderbook-agg) are stored as `double` in CSV. These are aggregated metrics and signals where money-exact precision is unnecessary, and `double` matches the runtime `FeedSeries.Columns` (`double[][]`) representation directly — no scaling/descaling overhead on load
- **Schema file per asset** — each `{symbol}[_{type}]/` directory contains a `feeds.json` file describing all available data feeds, their columns, intervals, and auto-apply configuration. Deserialized into existing `FeedMetadata` class (`Domain/History/FeedMetadata.cs`) and used to build `DataFeedSchema` + `BacktestFeedContext` registrations
- **UTC timestamps** — millisecond epoch, stored as `int64` (in all feed types including double-valued feeds)

### 3.2 Differences from Existing CandleIngestor Storage

The existing `CsvInt64BarLoader` / `CsvCandleWriter` pipeline uses a different layout and format. This document defines a **new** storage format for Binance futures data that will require a new loader implementation.

| Aspect | Existing (`CsvInt64BarLoader`) | This spec |
|--------|-------------------------------|-----------|
| Root | `%LOCALAPPDATA%/AlgoTradeForge/Candles` | `History/` (configurable) |
| Path | `{exchange}/{symbol}/{year}/{YYYY-MM}.csv` | `{exchange}/{symbol}[_{type}]/{feed_name}/{YYYY-MM}[_{interval}].csv` |
| Timestamp col | `Timestamp` — ISO 8601 string (`2025-01-01T00:00:00+00:00`) | `ts` — int64 epoch ms |
| Year subfolder | Yes (`/{year}/`) | No — flat under feed directory |
| Interval in path | No | Yes — `_{interval}` suffix on filename |
| CSV header | `Timestamp,Open,High,Low,Close,Volume` | `ts,o,h,l,c,vol` |

The existing `CsvInt64BarLoader` (`Infrastructure/CandleIngestion/CsvInt64BarLoader.cs`) will continue to serve spot candle data. A new loader will implement `IInt64BarLoader` for the futures path layout, or the existing loader will be extended to support both formats.

### 3.3 Directory Structure

```
History/
  binance/
    BTCUSDT_fut/
      feeds.json                  # Schema file — describes all feeds for this asset
      candles/
        2024-01_1m.csv          # M1 collection — standard OHLCV
        2024-01_1d.csv          # D1 collection
        2024-02_1m.csv
      candle-ext/
        2024-01_1m.csv          # Extended kline fields (q_vol, trades, taker volumes)
        2024-01_1d.csv
      mark-price/
        2024-01_1h.csv          # H1 collection
      funding-rate/
        2024-01.csv             # No suffix — fixed 8h event schedule
      open-interest/
        2024-01_5m.csv          # 5m collection
      ls-ratio-global/
        2024-01_15m.csv         # 15m collection
      ls-ratio-top-accounts/
        2024-01_15m.csv
      ls-ratio-top-positions/
        2024-01_1h.csv          # 1h collection
      taker-volume/
        2024-01_15m.csv
      liquidations/
        2024-01.csv             # No suffix — raw events
      orderbook-agg/
        2024-01_1m.csv          # 1m snapshot collection
    BTCUSDT/
      candles/
        2024-01_1m.csv          # Spot — no type suffix
    ETHUSDT_fut/
      candles/
        2024-01_1m.csv
      funding-rate/
        2024-01.csv
      ...
```

**Path pattern**: `History/{exchange}/{symbol}[_{type}]/{feed_name}/{YYYY-MM}[_{interval}].csv`

Where:
- `{exchange}` — lowercase exchange name (e.g., `binance`)
- `{symbol}[_{type}]` — trading pair with optional market type suffix. No suffix = spot (e.g., `BTCUSDT`). `_fut` = perpetual futures (e.g., `BTCUSDT_fut`)
- `{feed_name}` — data series name (e.g., `candles`, `open-interest`, `funding-rate`)
- `[_{interval}]` — optional timeframe suffix, present only for resolution-dependent feeds

### 3.4 Schema File (`feeds.json`)

Each `{symbol}[_{type}]/` directory contains a `feeds.json` describing candle properties and all auxiliary data feeds. The `feeds` section maps to the existing `FeedMetadata.Feeds` dictionary (`Domain/History/FeedMetadata.cs` → `Dictionary<string, FeedDefinition>`). The top-level `candles` section is new and will require extending `FeedMetadata` with a `Candles` property (or a separate `CandleConfig` class). Used to build `DataFeedSchema` + `BacktestFeedContext` registrations at backtest startup.

Example for `BTCUSDT_fut/feeds.json`:

```json
{
  "candles": {
    "multiplier": 1e8,
    "intervals": ["1m", "1d"]
  },
  "feeds": {
    "candle-ext": {
      "interval": "1m",
      "columns": ["q_vol", "trades", "taker_buy_vol", "taker_buy_q_vol"]
    },
    "funding-rate": {
      "interval": "",
      "columns": ["rate", "mark_price"],
      "autoApply": {
        "type": "FundingRate",
        "rateColumn": "rate"
      }
    },
    "open-interest": {
      "interval": "5m",
      "columns": ["oi", "oi_usd"]
    },
    "ls-ratio-global": {
      "interval": "15m",
      "columns": ["long_pct", "short_pct", "ratio"]
    },
    "ls-ratio-top-accounts": {
      "interval": "15m",
      "columns": ["long_pct", "short_pct", "ratio"]
    },
    "ls-ratio-top-positions": {
      "interval": "1h",
      "columns": ["long_pct", "short_pct", "ratio"]
    },
    "taker-volume": {
      "interval": "15m",
      "columns": ["buy_vol_usd", "sell_vol_usd", "ratio"]
    },
    "liquidations": {
      "interval": "",
      "columns": ["side", "price", "qty", "notional_usd"]
    },
    "mark-price": {
      "interval": "1h",
      "columns": ["o", "h", "l", "c"]
    },
    "orderbook-agg": {
      "interval": "1m",
      "columns": ["mid", "spread_bps", "bid_02", "ask_02", "bid_05", "ask_05", "bid_10", "ask_10", "bid_20", "ask_20", "imb_02", "imb_05", "imb_10"]
    }
  }
}
```

The top-level `candles` object defines the int64 precision multiplier and which intervals are collected. The `feeds` object describes all auxiliary data feeds: each key maps to a subdirectory name containing CSV files, `columns` lists non-timestamp columns, and `interval` is empty for event-based feeds (funding rate, liquidations).

### 3.5 CSV Schemas

All schemas use header rows. Candle data uses int64 with the precision multiplier from `feeds.json` (matching `Int64Bar` pipeline). All other feeds use `double` in natural units (matching `FeedSeries.Columns` runtime format — no conversion on load).

#### Candles (file: `{YYYY-MM}_1m.csv` / `{YYYY-MM}_1d.csv`) — int64

Standard OHLCV, consistent with existing `Int64Bar` / `CsvInt64BarLoader` pipeline. Precision multiplier is defined in `feeds.json` (not hardcoded per column).

```
ts,o,h,l,c,vol
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Open time, epoch ms |
| o | int64 | Price × multiplier |
| h | int64 | Price × multiplier |
| l | int64 | Price × multiplier |
| c | int64 | Price × multiplier |
| vol | int64 | Volume × multiplier |

#### Candle Extended Data (feed: `candle-ext`, file: `{YYYY-MM}_1m.csv` / `{YYYY-MM}_1d.csv`) — double

Additional per-candle fields from the Binance futures klines endpoint, stored as a separate auxiliary feed. Timestamps are aligned 1:1 with candle data. Supplied to strategy via `IFeedContext` as `FeedSeries`.

```
ts,q_vol,trades,taker_buy_vol,taker_buy_q_vol
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Open time, epoch ms (same as candle ts) |
| q_vol | double | Quote asset volume (USD) |
| trades | double | Number of trades |
| taker_buy_vol | double | Taker buy base volume |
| taker_buy_q_vol | double | Taker buy quote volume (USD) |

#### Funding Rate (file: `{YYYY-MM}.csv` — no interval suffix) — double

```
ts,rate,mark_price
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Funding timestamp, epoch ms |
| rate | double | Funding rate in natural units (e.g., 0.0001 = 0.01%) |
| mark_price | double | Mark price at funding time (USD) |

#### Open Interest (file: `{YYYY-MM}_5m.csv`) — double

```
ts,oi,oi_usd
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Timestamp, epoch ms |
| oi | double | Open interest in contracts (base asset) |
| oi_usd | double | Open interest value (USD) |

#### Long/Short Ratios (file: `{YYYY-MM}_15m.csv` / `{YYYY-MM}_1h.csv` — all three ratio types share same schema) — double

```
ts,long_pct,short_pct,ratio
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Timestamp, epoch ms |
| long_pct | double | Long percentage (e.g., 0.5482 = 54.82%) |
| short_pct | double | Short percentage |
| ratio | double | Long/short ratio |

#### Taker Buy/Sell Volume (file: `{YYYY-MM}_15m.csv`) — double

```
ts,buy_vol_usd,sell_vol_usd,ratio
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Timestamp, epoch ms |
| buy_vol_usd | double | Taker buy volume (USD) |
| sell_vol_usd | double | Taker sell volume (USD) |
| ratio | double | Buy/sell ratio |

#### Liquidations (file: `{YYYY-MM}.csv` — no interval suffix, raw events) — double

```
ts,side,price,qty,notional_usd
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Liquidation timestamp, epoch ms |
| side | double | 1.0 = long liquidated (forced sell), -1.0 = short liquidated (forced buy) |
| price | double | Average fill price (USD) |
| qty | double | Quantity in base asset |
| notional_usd | double | USD notional value |

#### Aggregated Order Book (file: `{YYYY-MM}_1m.csv` — see Section 4) — double

```
ts,mid,spread_bps,bid_02,ask_02,bid_05,ask_05,bid_10,ask_10,bid_20,ask_20,imb_02,imb_05,imb_10
```

All columns except `ts` are `double`. Schema details in Section 4.3.

---

## 4. Order Book Aggregation Strategy

### 4.1 Why Not Store Raw Snapshots

A full 1000-level order book snapshot for one symbol contains ~2000 rows (1000 bids + 1000 asks). At one snapshot per minute for 50 symbols, that's 100,000 rows/minute or 144 million rows/day. Stored as CSV, this is approximately 15-20 GB/day uncompressed.

For H1-D1 strategies, this data volume is unjustifiable. The individual price levels at position 847 in the book have no predictive power at hourly timeframes — they change hundreds of times per hour and reflect microstructure noise, not directional conviction.

What *does* have predictive power at H1-D1 is the *aggregate* shape of the book: how much total liquidity sits within 0.2% of mid, vs. 0.5%, vs. 1%, vs. 2%. This collapses a 2000-row snapshot into ~12 scalar values.

### 4.2 Aggregation Method: Depth Bands

For each snapshot, compute the total bid and ask depth (in USD) within fixed percentage bands from the mid-price:

```
mid_price = (best_bid + best_ask) / 2

For each band ∈ {0.2%, 0.5%, 1.0%, 2.0%}:
  bid_depth_{band} = sum(qty * price) for all bids where price >= mid * (1 - band)
  ask_depth_{band} = sum(qty * price) for all asks where price <= mid * (1 + band)
```

Derived metrics computed per snapshot:

```
spread        = best_ask - best_bid  (or as bps: spread / mid * 10000)
imbalance_{band} = (bid_depth_{band} - ask_depth_{band}) / (bid_depth_{band} + ask_depth_{band})
```

Imbalance ranges from -1.0 (all liquidity is on the ask side = sell wall) to +1.0 (all liquidity on bid side = buy wall). Values near 0 indicate balanced books.

### 4.3 Collection Pipeline

#### Step 1: Live Snapshot Collection

Poll `GET /fapi/v1/depth` with `limit=500` (sufficient for 2% band on BTC) every **1 minute** per symbol. This is within API rate limits (2400 request weight/min; depth at limit=500 costs weight 10 = 6 requests/sec capacity).

Request weight budget for 50 symbols at 1-minute intervals: 50 × 10 weight = 500 weight/minute, well within the 2400/min limit.

#### Step 2: Immediate Aggregation

Aggregate each raw snapshot into depth-band metrics immediately upon receipt. Do **not** store the raw snapshot — store only the aggregated row.

Why immediate aggregation rather than store-then-aggregate:
- Raw snapshot storage costs ~300 KB/snapshot vs. ~200 bytes/aggregated row (1500:1 ratio)
- Aggregation is stateless and deterministic — no information is gained by deferring it
- If you later decide you need a different band width, you'd need to re-collect anyway since the raw snapshots weren't stored

Exception: For a **validation period** of 7-14 days during initial deployment, store both raw snapshots (to a temporary directory) and aggregated data. This lets you verify the aggregation logic, test alternative band widths, and confirm the 500-level depth is sufficient. Delete raw snapshots after validation.

#### Step 3: Store Aggregated Row

Append one row per snapshot to the `orderbook-agg` CSV:

```
ts,mid,spread_bps,bid_02,ask_02,bid_05,ask_05,bid_10,ask_10,bid_20,ask_20,imb_02,imb_05,imb_10
```

| Column | Type | Description |
|--------|------|-------------|
| ts | int64 | Snapshot timestamp, epoch ms |
| mid | double | Mid-price (USD) |
| spread_bps | double | Spread in basis points |
| bid_02 | double | Total bid depth within 0.2% of mid (USD) |
| ask_02 | double | Total ask depth within 0.2% of mid (USD) |
| bid_05 | double | Bid depth within 0.5% (USD) |
| ask_05 | double | Ask depth within 0.5% (USD) |
| bid_10 | double | Bid depth within 1.0% (USD) |
| ask_10 | double | Ask depth within 1.0% (USD) |
| bid_20 | double | Bid depth within 2.0% (USD) |
| ask_20 | double | Ask depth within 2.0% (USD) |
| imb_02 | double | Book imbalance at 0.2% band (-1.0 to +1.0) |
| imb_05 | double | Book imbalance at 0.5% band (-1.0 to +1.0) |
| imb_10 | double | Book imbalance at 1.0% band (-1.0 to +1.0) |

Storage cost: 50 symbols × 1440 rows/day × ~140 bytes/row ≈ **10 MB/day** uncompressed. Trivial.

#### Step 4: Higher-Timeframe Aggregation (Post-Processing)

For H1 bars, compute from the 60 one-minute snapshots within each hour:

```
For each depth column (e.g., bid_02):
  mean_{col}    = average over 60 snapshots
  min_{col}     = minimum over 60 snapshots  (thinnest liquidity moment)
  max_{col}     = maximum over 60 snapshots  (deepest liquidity moment)

For imbalance columns:
  mean_imb_{band} = average imbalance over the hour
  std_imb_{band}  = standard deviation (measures how volatile the imbalance was)

For spread:
  mean_spread   = average spread over the hour
  max_spread    = widest spread (marks stressed moments)
```

This produces an H1 aggregated row that captures not just the average book shape, but the *range* of book states within the hour — a thin-book moment during the hour is meaningful even if the average looks healthy.

### 4.4 Band Width Selection Rationale

The four bands (0.2%, 0.5%, 1.0%, 2.0%) are chosen for H1-D1 trading because:

- **0.2%** (~$200 on a $100k BTC): Immediate execution zone. Reflects liquidity available for a market order without significant slippage. Imbalance at this level is the highest-signal short-term indicator but decays fast.
- **0.5%** (~$500 on BTC): Typical intra-hour range during low-vol periods. Depth here reflects standing institutional interest. Imbalance at this level is the sweet spot for H1 strategies.
- **1.0%** (~$1000 on BTC): Typical H4 candle range. Depth here captures large resting orders / iceberg activity. Relevant for multi-hour holding periods.
- **2.0%** (~$2000 on BTC): Approximate daily range during moderate volatility. Depth here shows the "structural" support/resistance from the book. Most relevant for D1 strategies.

For non-BTC pairs with higher volatility (e.g., altcoins), the same percentage bands remain appropriate because the bands scale with price automatically.

---

## 5. Usefulness Assessment: Aggregated Order Book Data for H1-D1 Strategies

### 5.1 What the Research and Practice Shows

Aggregated order book metrics at minute-to-hourly resolution have demonstrated value in several strategy contexts:

**Imbalance as a directional predictor**: A persistent bid-heavy imbalance at the 0.5% level (imb_05 > 0.3 sustained over 2+ hours) has positive correlation with subsequent 4-24h price direction in crypto futures. This isn't a standalone signal but strengthens trend-following entries — a momentum signal confirmed by book imbalance has meaningfully higher win rates than momentum alone.

**Depth thinning as a volatility predictor**: When total depth within 1.0% drops below its 7-day rolling average by more than 1 standard deviation, the probability of a large move in the next 4-12 hours increases significantly. This is a regime indicator rather than a directional signal — it tells you *when* to trade, not *what* to trade.

**Spread widening as a stress indicator**: Spread expansion at the minute level, aggregated to hourly averages, is a leading indicator of liquidation cascades. Sustained mean_spread above 2× its 24h average often precedes large OI drops within 2-6 hours.

**Imbalance divergence from price**: When price makes a new high but bid-side depth at 0.5% is declining (less resting buy interest supporting the higher price), this divergence pattern is a reversal signal at the H4-D1 timeframe. Analogous to volume divergence in traditional TA but more granular.

### 5.2 What Aggregated Book Data Does NOT Capture

**Spoofing dynamics**: Large orders placed and cancelled within seconds to manipulate book appearance. These are invisible at 1-minute snapshot frequency. Irrelevant for H1-D1 strategies anyway — spoof impact decays in milliseconds to seconds.

**Queue position and order flow toxicity**: Which orders are getting filled first, whether there's adverse selection. These are HFT concerns.

**Cross-exchange arbitrage signals**: The book state on Binance tells you nothing about Bybit or OKX depth. For cross-exchange strategies, you'd need separate collection per venue.

### 5.3 Practical Recommendation

For H1-D1 strategies, aggregated order book data is a **second-tier signal** — valuable as a confirmation/filter layer on top of primary signals (price action, OI changes, funding rate, taker volume), but not sufficient as a standalone alpha source.

Prioritize building strategies on Tier 1 and Tier 2 metrics first (OHLCV, funding rate, OI, taker volume, long/short ratios). Add order book aggregates as a refinement layer once the primary signals are established and you have enough collected history (minimum 3 months of 1-minute snapshots) to validate the book-based features in backtesting.

The collection pipeline should be deployed from day one regardless — the marginal cost is near zero (10 MB/day, one API call per symbol per minute), and the data cannot be backfilled. Starting collection now means you'll have the history available when you're ready to integrate it.

---

## 6. Collection Schedule and API Budget

### 6.1 Polling Schedule

| Metric | Interval | Symbols | Requests/Hour | Weight/Hour |
|--------|---------|---------|--------------|------------|
| Klines (M1) | 1 min | 50 | 3,000 | 15,000 |
| Open Interest | 5 min | 50 | 600 | 3,000 |
| LS Ratio Global | 15 min | 50 | 200 | 1,000 |
| LS Ratio Top Accts | 15 min | 50 | 200 | 1,000 |
| Taker Volume | 15 min | 50 | 200 | 1,000 |
| LS Ratio Top Pos | 1 hr | 50 | 50 | 250 |
| Liquidations | 4 hr | 50 | 13 | 325 |
| Order Book Depth | 1 min | 50 | 3,000 | 30,000 |
| Funding Rate | 8 hr | 50 | 7 | 35 |
| Mark Price Klines | 1 hr | 50 | 50 | 250 |

**Total**: ~7,320 requests/hour, ~51,860 weight/hour

Binance rate limit: 2,400 weight/minute = 144,000 weight/hour. Budget utilization: **~36%**. Comfortable headroom for retries, burst traffic, and other API usage.

Note: M1 klines and order book depth dominate the budget. If running tight on rate limits (e.g., sharing the API key with live trading), reduce order book polling to every 2 minutes (halves that component) or reduce the symbol list.

### 6.2 Backfill Plan

| Metric | Backfill Source | Depth | Estimated Time |
|--------|----------------|-------|---------------|
| Klines (D1) | `/fapi/v1/klines` | Full (2019+) | ~15 minutes |
| Klines (H1) | `/fapi/v1/klines` | Full (2019+) | ~6 hours? |
| Klines (M1) | `/fapi/v1/klines` | Full (2019+) | ~8 hours for 50 symbols × 5 years |
| Funding Rate | `/fapi/v1/fundingRate` | Full (2019+) | ~10 minutes for 50 symbols |
| Mark Price | `/fapi/v1/markPriceKlines` | Full (2019+) | ~4 hours at H1 resolution |
| Open Interest | `/futures/data/openInterestHist` | 30 days only | ~5 minutes (initial seed) |
| LS Ratios | `/futures/data/*` | 30 days only | ~5 minutes each |
| Taker Volume | `/futures/data/takeBuySellVol` | 30 days only | ~5 minutes |
| Liquidations | `/fapi/v1/allForceOrders` | 7 days only | ~2 minutes |
| Order Book | Not backfillable | — | — |

**Backfill from third-party for pre-30-day history**: For open interest, long/short ratios, taker volume, and liquidation data older than 30 days, consider Tardis.dev (data from 2020-05) or CoinGlass API (varies). This is optional — the forward-collected data will become sufficient for backtesting within 3-6 months.

### 6.3 Initial Deployment Sequence

1. **Day 1**: Deploy forward collection for all Tier 2 metrics (OI, ratios, taker volume) and order book aggregation. These are the time-critical components — every day of delay is data lost forever.
2. **Day 1-2**: Run backfill for Tier 1 data (klines, funding rate, mark price). These are not time-critical since they're fully backfillable.
3. **Day 1-7**: Validation period — store raw order book snapshots alongside aggregated data. Compare, tune band widths if needed.
4. **Day 7+**: Delete raw snapshots, run aggregated-only pipeline.
5. **Day 30+**: First meaningful backtesting window for Tier 2 metrics becomes available.
6. **Month 3+**: Sufficient history for reliable H1-D1 strategy development incorporating derivatives features.

---

## 7. Symbol Universe

### 7.1 Initial Watchlist (50 symbols)

Select based on:
- 24h futures volume > $100M USD (ensures meaningful OI and book depth)
- Perpetual contract type (USDT-margined)
- Listing age > 6 months (sufficient history for klines/funding backfill)

Recommended starting set (top by volume, periodically reviewed):

**Major pairs (always included)**: BTCUSDT, ETHUSDT, BNBUSDT, SOLUSDT, XRPUSDT, DOGEUSDT, ADAUSDT, AVAXUSDT, DOTUSDT, LINKUSDT

**High-volume mid-caps (rotate quarterly)**: MATICUSDT, NEARUSDT, APTUSDT, ARBUSDT, OPUSDT, SUIUSDT, PEPEUSDT, WIFUSDT, FTMUSDT, ATOMUSDT, INJUSDT, TIAUSDT, SEIUSDT, JUPUSDT, ENAUSDT, WLDUSDT, ONDOUSDT, RENDERUSDT, FETUSDT, ARUSDT, etc.

The symbol list should be configurable and hot-reloadable without service restart.

### 7.2 Dynamic Universe Management

Crypto pairs are ephemeral — new perpetual contracts are listed frequently, and lower-volume pairs may be delisted. The CandleIngestor should:

- Periodically query `GET /fapi/v1/exchangeInfo` to detect new perpetual listings
- Alert on new listings that meet the volume threshold
- Continue collecting data for delisted symbols until their contracts expire
- Maintain a `symbols.json` configuration file that can be updated without redeployment

---

## 8. Storage Volume Estimates

### 8.1 Per-Symbol Daily Volume

| Metric | Resolution | Records/Day | Bytes/Record | MB/Day |
|--------|-----------|-------------|-------------|--------|
| Klines M1 | 1 min | 1,440 | 100 | 0.14 |
| Mark Price H1 | 1 hr | 24 | 56 | 0.001 |
| Funding Rate | 8 hr | 3 | 28 | <0.001 |
| Open Interest | 5 min | 288 | 28 | 0.008 |
| LS Ratio Global | 15 min | 96 | 20 | 0.002 |
| LS Ratio Top Accts | 15 min | 96 | 20 | 0.002 |
| LS Ratio Top Pos | 1 hr | 24 | 20 | <0.001 |
| Taker Volume | 15 min | 96 | 28 | 0.003 |
| Liquidations | varies | ~50-500 | 44 | 0.02 |
| Order Book Agg | 1 min | 1,440 | 140 | 0.20 |

**Per-symbol total**: ~0.38 MB/day

### 8.2 Full Universe (50 Symbols)

- **Daily**: ~19 MB
- **Monthly**: ~570 MB
- **Yearly**: ~6.8 GB
- **5 years (with M1 kline backfill)**: ~40 GB

Including the M1 kline backfill (5 years × 50 symbols × 365 days × 1440 records × 100 bytes ≈ 13 GB), total storage requirement for the initial deployment is approximately **20 GB** for backfilled history plus ~19 MB/day forward growth.

This is trivially manageable on any Hetzner instance or local SSD.

---

## Appendix A: Binance API Endpoint Quick Reference

| Endpoint | Path | Weight | Rate Pool |
|----------|------|--------|-----------|
| Futures Klines | `GET /fapi/v1/klines` | 5 | IP |
| Mark Price Klines | `GET /fapi/v1/markPriceKlines` | 5 | IP |
| Funding Rate History | `GET /fapi/v1/fundingRate` | 500/5min shared | IP |
| Open Interest (current) | `GET /fapi/v1/openInterest` | 1 | IP |
| Open Interest Statistics | `GET /futures/data/openInterestHist` | 5 | IP |
| Long/Short Ratio (global) | `GET /futures/data/globalLongShortAccountRatio` | 5 | IP |
| Top LS Ratio (accounts) | `GET /futures/data/topLongShortAccountRatio` | 5 | IP |
| Top LS Ratio (positions) | `GET /futures/data/topLongShortPositionRatio` | 5 | IP |
| Taker Buy/Sell Volume | `GET /futures/data/takeBuySellVol` | 5 | IP |
| Liquidation Orders | `GET /fapi/v1/allForceOrders` | 20/50 | IP |
| Order Book Depth | `GET /fapi/v1/depth` | 5/10/20 (by limit) | IP |
| Exchange Info | `GET /fapi/v1/exchangeInfo` | 1 | IP |

All endpoints use the base URL `https://fapi.binance.com` for USDT-M futures.

## Appendix B: Third-Party Data Sources for Backfill

| Provider | Coverage | OI History | Funding | Book Data | Pricing |
|----------|----------|-----------|---------|-----------|---------|
| Tardis.dev | 2020-02+ | 2020-05+ | Yes | Tick-level depth | Free (1st of month), paid plans from ~$50/mo |
| CoinGlass | 2020+ | Yes (aggregated) | Yes | No | API plans from ~$50/mo |
| Amberdata | 2019-09+ | Yes | Yes | 1-min snapshots | Enterprise ($$) |
| CoinAPI | 2021-01+ | Select exchanges | Yes | No | Metered from ~$79/mo |
| Binance Bulk Downloads | Varies | No | No | No | Free (klines and trades only) |