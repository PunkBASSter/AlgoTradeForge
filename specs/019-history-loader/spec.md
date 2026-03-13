# Feature Specification: History Loader — Binance Futures Vertical Slice

**Feature Branch**: `019-history-loader`
**Created**: 2026-03-13
**Status**: Draft
**Input**: User description: "History Loader as CandleIngestor replacement — Binance Futures derivatives metrics collection pipeline"

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Backfill Tier 1 Futures History (Priority: P1)

As a strategy researcher, I want to backfill complete OHLCV klines and funding rate history for Binance USDT-M futures symbols from contract inception to present, so I can develop and backtest derivatives strategies with full price and funding history.

**Why this priority**: Without OHLCV candle data and funding rates, no futures backtesting is possible. These are the foundational data feeds — everything else builds on top. Both are fully backfillable (no data loss from delayed deployment) but are the prerequisite for any strategy work.

**Independent Test**: Can be fully tested by configuring a single symbol (e.g., BTCUSDT perpetual), running a backfill, and verifying the output CSV files contain correct OHLCV data and funding rates from 2019-09 to present. The data can be cross-checked against Binance's public charts.

**Acceptance Scenarios**:

1. **Given** a newly configured symbol with history start of 2019-09-01, **When** the operator triggers a backfill, **Then** the system downloads all available 1-minute and 1-day klines from Binance and writes them as monthly-partitioned CSV files in the configured data directory.
2. **Given** a symbol with kline backfill in progress, **When** the process is interrupted and restarted, **Then** the system resumes from the last successfully written timestamp without re-downloading already stored data.
3. **Given** a configured symbol, **When** a funding rate backfill runs, **Then** all funding rate events (every 8 hours since contract inception) are stored in monthly CSV files with rate and mark price values.
4. **Given** a backfill has completed, **When** the operator inspects the output directory, **Then** a per-asset schema file (`feeds.json`) exists describing all collected feeds, their column names, and intervals.

---

### User Story 2 — Forward Collection of Time-Limited Data (Priority: P2)

As a strategy researcher, I want the system to automatically collect open interest, long/short ratios, and taker buy/sell volume on a periodic schedule, so that this 30-day-limited data is preserved before it expires from the Binance API.

**Why this priority**: Tier 2 metrics (open interest, positioning ratios, taker volume) are the most valuable derivatives signals for H1-D1 strategies, but Binance only retains 30 days of history. Every day without collection is data permanently lost. This is the most time-critical component.

**Independent Test**: Can be tested by starting the service with a configured symbol list, waiting for one scheduled collection cycle, and verifying that open interest (5m), long/short ratios (15m), and taker volume (15m) CSV files are created with correct timestamps and values.

**Acceptance Scenarios**:

1. **Given** a running service with configured symbols, **When** a scheduled collection cycle fires, **Then** the system fetches the latest open interest data at 5-minute resolution and appends new records to the appropriate monthly CSV file.
2. **Given** a running service, **When** a collection cycle completes, **Then** global long/short account ratios, top trader account ratios, and taker buy/sell volume are each collected at 15-minute resolution and stored.
3. **Given** the service has been collecting data for 25 days, **When** the operator queries the status, **Then** the system reports 25 days of continuous data with no gaps for all Tier 2 feeds.
4. **Given** a network interruption during collection, **When** the next cycle runs, **Then** the system detects the gap and backfills all available data within the 30-day API window.

---

### User Story 3 — Backtest Engine Consumption of Auxiliary Feeds (Priority: P3)

As a strategy researcher, I want collected derivatives data to be automatically loadable by the backtest engine as auxiliary data feeds, so my strategies can access funding rates, open interest, and other signals during backtesting without manual data preparation.

**Why this priority**: Data collection without consumption has no value. The collected data must integrate with the existing `FeedSeries` / `IFeedContext` backtest infrastructure. This story closes the loop from collection to strategy use.

**Independent Test**: Can be tested by collecting at least one day of data for a symbol, then running a backtest with a strategy that subscribes to an auxiliary feed (e.g., funding rate). The strategy should receive feed values via `IFeedContext.TryGetLatest()`.

**Acceptance Scenarios**:

1. **Given** a symbol with collected funding rate history, **When** a backtest is configured for that symbol, **Then** the backtest engine reads `feeds.json` and automatically registers all available data feeds as `DataFeedSchema` entries.
2. **Given** registered auxiliary feeds, **When** the backtest engine advances to a new bar, **Then** strategies can access the latest funding rate, open interest, and ratio values through the standard `IFeedContext` interface.
3. **Given** a candle-aligned auxiliary feed (e.g., `candle-ext` with taker volumes), **When** the backtest loads data, **Then** the auxiliary feed timestamps align 1:1 with the corresponding candle timestamps.
4. **Given** a funding rate feed with `autoApply` configuration, **When** the backtest engine processes bars, **Then** funding rate cash flows are automatically applied to open positions per the existing auto-apply mechanism.

---

### User Story 4 — Mark Price and Extended Kline Data (Priority: P4)

As a strategy researcher, I want mark price klines and extended candle data (quote volume, trade count, taker buy volumes) to be collected, so I can calculate futures basis (mark vs. last price) and analyze trade flow decomposition in my strategies.

**Why this priority**: Mark price enables basis calculations (a key derivatives signal), and extended kline fields provide buy/sell pressure decomposition without a separate endpoint. Both are fully backfillable, so they're valuable but less urgent than the time-limited Tier 2 data.

**Independent Test**: Can be tested by backfilling mark price at 1-hour resolution for one symbol and verifying the basis spread can be computed. Extended kline data can be verified by comparing `taker_buy_vol` + computed `taker_sell_vol` against total `volume`.

**Acceptance Scenarios**:

1. **Given** a configured symbol, **When** mark price backfill runs, **Then** mark price OHLC klines at 1-hour resolution are stored in monthly CSV files.
2. **Given** a kline fetch for a futures symbol, **When** the response includes extended fields (quote volume, trade count, taker buy volumes), **Then** these are stored in a separate `candle-ext` feed alongside the standard OHLCV data.
3. **Given** mark price and last-trade-price data exist, **When** a strategy queries both feeds, **Then** it can compute the basis spread (last price - mark price) for any given timestamp.

---

### User Story 5 — Configuration and Monitoring (Priority: P5)

As a system operator, I want to configure which symbols and data feeds to collect via a configuration file, view collection status, and receive alerts about data gaps or failures, so I can ensure the system operates reliably without constant supervision.

**Why this priority**: Operational visibility is essential for a background service that collects irreplaceable time-limited data. However, basic collection functionality must exist before monitoring adds value.

**Independent Test**: Can be tested by modifying the symbol configuration while the service is running and verifying the new symbol is picked up on the next collection cycle. Status can be verified by inspecting the per-feed status files.

**Acceptance Scenarios**:

1. **Given** a configuration file listing 50 symbols with their data feed settings, **When** the service starts, **Then** it begins collecting all configured feeds for all listed symbols according to their specified schedules.
2. **Given** a running service, **When** the operator adds a new symbol to the configuration, **Then** the service detects the change and begins collecting for the new symbol without requiring a restart.
3. **Given** a running service, **When** a collection cycle completes, **Then** each data feed updates a status file recording the last successfully loaded timestamp and any detected data gaps.
4. **Given** a data gap is detected in a Tier 2 feed, **When** the gap falls within the 30-day API window, **Then** the system automatically backfills the gap on the next collection cycle.

---

### User Story 6 — Top Trader Position Ratios and Liquidation Data (Priority: P6)

As a strategy researcher, I want top trader long/short position ratios (1-hour resolution) and liquidation event history to be collected, so I can analyze smart money positioning divergence and liquidation cascades in my strategies.

**Why this priority**: Position ratios at 1-hour resolution and liquidation data add additional signal layers. Position ratios have a 30-day window; liquidations have only a 7-day window. Less critical than the core Tier 2 feeds but still valuable for advanced strategies.

**Independent Test**: Can be tested by collecting data for one cycle and verifying that top trader position ratios contain `long_pct`, `short_pct`, `ratio` columns, and liquidation events contain `side`, `price`, `qty`, `notional_usd` fields.

**Acceptance Scenarios**:

1. **Given** a configured symbol, **When** the collection schedule fires, **Then** top trader long/short position ratios at 1-hour resolution are collected and stored.
2. **Given** a configured symbol, **When** the liquidation collection runs (every 4 hours), **Then** all forced liquidation events within the API's 7-day window are stored as raw events with side, price, quantity, and USD notional value.
3. **Given** the liquidation collection ran 4 hours ago, **When** the next cycle fires, **Then** only new events since the last collected timestamp are appended (no duplicates).

---

### User Story 7 — Spot Candle Ingestion Migration (Priority: P7)

As a system operator, I want the History Loader to ingest spot OHLCV klines from Binance using the new storage format, so the existing CandleIngestor can be retired and all candle data flows through a single service.

**Why this priority**: Spot ingestion is already solved by the CandleIngestor and can continue running during development. Migrating it into the History Loader unifies the data pipeline but is not blocking for futures strategy work. It becomes important once the new storage format is established and the backtest engine can read it.

**Independent Test**: Can be tested by configuring a spot symbol (e.g., BTCUSDT without `_fut` suffix), running a backfill, and verifying OHLCV files are written in the new directory layout. Cross-check against existing CandleIngestor output for the same date range.

**Acceptance Scenarios**:

1. **Given** a configured spot symbol with history start date, **When** a backfill runs, **Then** OHLCV klines are stored in the new format (`History/binance/BTCUSDT/candles/{YYYY-MM}_1m.csv`).
2. **Given** spot and futures symbols in the same configuration, **When** collection runs, **Then** spot symbols collect only OHLCV klines while futures symbols collect OHLCV plus all applicable derivatives feeds.

---

### Edge Cases

- What happens when a Binance API rate limit (HTTP 429) is hit during collection? The system must back off, retry with exponential delay, and resume without data loss.
- What happens when a symbol is delisted mid-collection? The system must handle HTTP 400 errors gracefully and mark the symbol as inactive without affecting other symbols.
- What happens when the output disk is full? The system must detect write failures and alert the operator rather than silently dropping data.
- What happens when the system restarts mid-month? The system must resume from the last written timestamp in the current month's CSV file.
- What happens when Binance returns empty results for a time range? The system must record the gap and retry on subsequent cycles rather than treating it as "no data exists."
- What happens when multiple data feeds have different collection intervals (5m, 15m, 1h)? Each feed must maintain its own independent schedule and status tracking.
- What happens when a month boundary occurs during a collection run? The writer must seamlessly switch to the new monthly partition file.
- What happens when the same timestamp is received twice (e.g., after a restart)? The system must deduplicate — silently skip records with timestamps already present.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST collect OHLCV klines from both the Binance Spot API and USDT-M Futures API at 1-minute and 1-day resolution for all configured symbols.
- **FR-001a**: System MUST support both spot symbols (e.g., `BTCUSDT`) and perpetual futures symbols (e.g., `BTCUSDT_fut`) in the same configuration, with derivatives-specific feeds only applicable to futures symbols.
- **FR-002**: System MUST collect funding rate history (rate and mark price at each 8-hour funding event) for all configured symbols.
- **FR-003**: System MUST collect open interest statistics at 5-minute resolution for all configured symbols.
- **FR-004**: System MUST collect global long/short account ratios at 15-minute resolution for all configured symbols.
- **FR-005**: System MUST collect top trader long/short account ratios at 15-minute resolution for all configured symbols.
- **FR-006**: System MUST collect taker buy/sell volume at 15-minute resolution for all configured symbols.
- **FR-007**: System MUST collect top trader long/short position ratios at 1-hour resolution for all configured symbols.
- **FR-008**: System MUST collect forced liquidation events (every 4 hours) for all configured symbols.
- **FR-009**: System MUST collect mark price klines at 1-hour resolution for all configured symbols.
- **FR-010**: System MUST store extended kline data (quote volume, trade count, taker buy base/quote volumes) as a separate auxiliary feed alongside OHLCV candles.
- **FR-011**: System MUST store all data as flat monthly-partitioned CSV files following the directory structure: `{root}/{exchange}/{symbol}[_{type}]/{feed_name}/{YYYY-MM}[_{interval}].csv`.
- **FR-012**: System MUST use int64-encoded values (with configurable precision scale factor) for OHLCV candle data and double-precision floating point for all auxiliary feeds.
- **FR-013**: System MUST use UTC millisecond epoch timestamps (int64) as the first column in all CSV files.
- **FR-014**: System MUST generate and maintain a `feeds.json` schema file per asset directory describing all available data feeds, their columns, intervals, and auto-apply configuration.
- **FR-015**: System MUST support full backfill of Tier 1 data (klines, funding rate, mark price) from contract inception to present.
- **FR-016**: System MUST support periodic forward collection of Tier 2 data (open interest, ratios, taker volume) with automatic gap detection and backfill within the 30-day API window.
- **FR-017**: System MUST respect Binance API rate limits via a two-tier rate limiter: a global rate limiter enforcing the overall API weight budget, and per-source rate limiters for individual endpoint types. MUST implement exponential backoff on HTTP 429 responses.
- **FR-017a**: System MUST support bounded parallel backfill with configurable concurrency (e.g., 3-5 symbols concurrently), coordinated through the global rate limiter.
- **FR-018**: System MUST resume from the last successfully collected timestamp after restart, without re-downloading already stored data.
- **FR-019**: System MUST maintain per-feed status tracking (last collected timestamp, known data gaps) persisted to disk.
- **FR-020**: System MUST process each symbol independently — a failure in one symbol's collection MUST NOT block or affect other symbols.
- **FR-021**: System MUST support hot-reloading of the symbol configuration without requiring a service restart.
- **FR-022**: The backtest engine MUST be able to load collected data (both OHLCV candles and auxiliary feeds) exclusively from the new storage format via `feeds.json` schema. The existing `CsvInt64BarLoader` (old format) is replaced, not extended.
- **FR-023**: Funding rate feeds with `autoApply` configuration MUST integrate with the existing `BacktestFeedContext` auto-apply mechanism to apply funding cash flows to open positions.
- **FR-024**: System MUST run as a Web API-hosted background service with both scheduled collection cycles and HTTP endpoints for on-demand operations (backfill triggers, status queries, health checks).
- **FR-025**: System MUST expose HTTP endpoints for: (a) triggering on-demand backfill for specific symbols/feeds, (b) querying collection status per symbol/feed, and (c) service health checks.

### Key Entities

- **Data Feed**: A named series of time-indexed records (e.g., "open-interest", "funding-rate"). Each feed has a fixed set of columns, a collection interval (or event-based), and a storage location.
- **Feed Schema** (`feeds.json`): Per-asset metadata file describing all available feeds — column names, intervals, precision scale factor for candles, and auto-apply configuration for feeds that affect positions.
- **Feed Status**: Per-feed tracking of collection progress — last successful timestamp, list of known data gaps, and collection health.
- **Asset Configuration**: A configured trading pair with exchange, market type (spot/perpetual), history start date, and list of feeds to collect with their respective intervals and priorities.
- **Collection Schedule**: Each feed type runs on its own independent timer at its defined interval (5m for OI, 15m for ratios/taker volume, 1h for position ratios/mark price, 4h for liquidations, daily for backfill). All timers coordinate through a shared global rate limiter.
- **Data Gap**: A detected discontinuity in a feed's time series where expected records are missing. Gaps within the API's history window are automatically backfilled; gaps beyond the window are flagged as permanent.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Complete OHLCV kline history (1-minute resolution) for 50 configured Binance futures symbols can be backfilled from contract inception to present.
- **SC-002**: All 30-day-limited feeds (open interest, long/short ratios, taker volume) are collected at their specified resolution with no data gaps longer than one collection interval during continuous operation.
- **SC-003**: After a system restart or network interruption, data collection resumes within one scheduled cycle with automatic gap backfill for any missed periods within the API window.
- **SC-004**: A strategy running in the backtest engine can access funding rates, open interest, and long/short ratio values through the standard auxiliary feed interface without any manual data preparation.
- **SC-005**: The service operates within 40% of the Binance API rate limit budget, leaving headroom for retries and other API usage.
- **SC-006**: Adding a new symbol to the configuration and having it begin collecting within the next scheduled cycle, without service restart.
- **SC-007**: All collected data for 50 symbols consumes less than 25 MB per day of storage (excluding initial backfill).
- **SC-008**: Backfill of Tier 1 data (klines + funding rate) for a single symbol from 2019 to present completes within 2 hours.

## Clarifications

### Session 2026-03-13

- Q: Hosting model — Web API host vs. plain worker service vs. worker + minimal health endpoint? → A: Web API host — background worker + HTTP endpoints for on-demand backfill, status queries, and health checks.
- Q: Backfill parallelism — sequential, bounded parallel, or full parallel symbol processing? → A: Bounded parallel (configurable concurrency) with a two-tier rate limiter: a global rate limiter for the overall Binance API budget and per-source rate limiters for individual endpoint/feed types.
- Q: Project location — new projects within AlgoTradeForge.slnx, separate sibling solution, or single project? → A: New projects within the existing `AlgoTradeForge.slnx`, mirroring CandleIngestor's pattern (e.g., `AlgoTradeForge.HistoryLoader`), with project references to Domain and Application layers.
- Q: Collection scheduling model — single base timer, independent per-feed-type timers, or cron-based job scheduler? → A: Independent per-feed-type timers — each feed type has its own schedule and runs independently, coordinated through the shared rate limiter.
- Q: CandleIngestor replacement scope — permanent coexistence, phased replacement, or include spot now? → A: Include spot now — this vertical slice also migrates spot candle ingestion into the History Loader, fully replacing the CandleIngestor.

## Assumptions

- The Binance USDT-M Futures API endpoints described in the requirements document remain stable and available (base URL: `https://fapi.binance.com`).
- The system will use a single API key shared with other services; rate limit budgeting assumes no more than 60% total utilization across all services.
- The initial symbol universe is 50 USDT-margined perpetual futures contracts, configurable and expandable.
- Third-party backfill sources (Tardis.dev, CoinGlass) for pre-30-day Tier 2 history are out of scope for this specification.
- Order book aggregation (Tier 3) is deferred to a future iteration — the infrastructure supports it but collection is not included in this vertical slice.
- Index price klines (Tier 1, low priority) are deferred — can be derived from spot data if needed.
- The History Loader fully replaces the existing CandleIngestor for both spot and futures data. All reads and writes use the new storage format exclusively. Existing spot history (2 assets from 2020) is negligible and can be re-backfilled; no backward compatibility with the old CandleIngestor format is required.
- The History Loader is built as new project(s) within the existing `AlgoTradeForge.slnx` solution, referencing Domain and Application layers directly. Assembly separation follows the same pattern as CandleIngestor.
- The History Loader shares the same data disk as the backtest engine (either local disk or network mount).
