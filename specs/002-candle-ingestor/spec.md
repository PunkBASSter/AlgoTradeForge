# Feature Specification: Candle Data History Ingestion Service

**Feature Branch**: `002-candle-ingestor`
**Created**: 2026-02-09
**Status**: Draft
**Input**: User description: "Use the candle-ingestor design doc to implement history ingestion functionality"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Initial Historical Backfill (Priority: P1)

As a backtest developer, I want the system to automatically fetch and store years of historical candle data from exchange APIs so that I have a complete dataset available locally for running backtests without manual data preparation.

**Why this priority**: Without historical data, no backtests can run. This is the foundational capability that enables all other features. A single successful backfill proves the entire pipeline works end-to-end.

**Independent Test**: Can be fully tested by configuring one asset (e.g., BTCUSDT) with a history start date, running the service once, and verifying that integer-encoded CSV files appear in the correct directory structure with accurate candle data covering the requested date range.

**Acceptance Scenarios**:

1. **Given** no existing candle data on disk and an asset configured with `HistoryStart: 2024-01-01`, **When** the ingestion service runs for the first time, **Then** CSV files are created partitioned by `{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv` containing all available 1-minute candles from 2024-01-01 to the current time, with all price and volume values stored as integers (multiplied by 10^DecimalDigits).

2. **Given** no existing candle data and two assets configured (BTCUSDT, ETHUSDT) on the same exchange, **When** the ingestion service runs, **Then** both assets are fetched sequentially (respecting rate limits) and each has its own complete directory tree of CSV files.

3. **Given** an asset configured with `DecimalDigits: 2`, **When** a candle with price `67432.15` is fetched from the exchange, **Then** it is stored in the CSV as `6743215` (price multiplied by 100).

4. **Given** an asset configured with `HistoryStart: 2023-06-15`, **When** the backfill runs, **Then** the first CSV partition file is `2023/2023-06.csv` and contains only candles from June 15 onward (not the full month).

---

### User Story 2 - Incremental Catch-Up Ingestion (Priority: P2)

As a backtest developer, I want the service to detect where historical data left off and fetch only missing candles so that I always have up-to-date data without re-downloading the entire history.

**Why this priority**: After the initial backfill, this is the primary ongoing operation. The service must be efficient and idempotent — it should be safe to restart at any time without creating duplicate data or gaps.

**Independent Test**: Can be tested by running the service once to create initial data, waiting for new candles to become available, running the service again, and verifying only the new candles were appended without duplicates.

**Acceptance Scenarios**:

1. **Given** existing CSV data with the last candle timestamp at `2025-01-15T10:30:00Z`, **When** the ingestion service runs, **Then** it fetches only candles starting from `2025-01-15T10:31:00Z` (last timestamp + interval) up to the current time and appends them to the appropriate partition files.

2. **Given** the service was offline for 3 days and the last stored candle is 3 days old, **When** the service runs, **Then** it detects the gap, fetches all missing candles for those 3 days, and logs a warning about the gap while proceeding normally.

3. **Given** existing data ending at `2025-01-31T23:59:00Z`, **When** new candles arrive in February, **Then** a new partition file `2025/2025-02.csv` is created with the header row and February candles, while the January file remains untouched.

4. **Given** the service crashes mid-write and is restarted, **When** it resumes, **Then** it reads the last timestamp from the existing CSV files and continues fetching from that point without creating duplicate rows (idempotent writes).

---

### User Story 3 - Scheduled Automated Ingestion (Priority: P3)

As a backtest developer, I want the ingestion service to run automatically on a configurable schedule so that candle data stays current without manual intervention.

**Why this priority**: Automation is important for a "set and forget" workflow, but can be deferred since manual runs (User Story 1 & 2) already deliver full value. The scheduling wraps the core ingestion in a repeating timer.

**Independent Test**: Can be tested by setting a short schedule interval (e.g., 1 minute), starting the service, and verifying it triggers ingestion runs at the configured interval, logging each scheduled run.

**Acceptance Scenarios**:

1. **Given** the service is configured with `ScheduleIntervalHours: 6` and `RunOnStartup: true`, **When** the service starts, **Then** it runs an immediate ingestion and then repeats every 6 hours.

2. **Given** the service is configured with `RunOnStartup: false`, **When** the service starts, **Then** no immediate ingestion occurs and the first run happens after the configured interval elapses.

3. **Given** the service is running and receives a shutdown signal, **When** cancellation is requested, **Then** the service completes the current in-progress API request gracefully (does not abandon a half-written CSV) and shuts down cleanly.

---

### User Story 4 - Candle Data Loading for Backtest Consumption (Priority: P4)

As a backtest engine, I need to load stored integer candle CSVs into `TimeSeries<IntBar>` objects efficiently so that backtests can run directly against the ingested data without any float conversion at runtime.

**Why this priority**: This is the read-side counterpart to ingestion. The backtest engine must consume the data the ingestor produces. While lower priority than writing data, it completes the end-to-end pipeline.

**Independent Test**: Can be tested by loading a known CSV file via the candle loader and verifying the resulting `TimeSeries<IntBar>` contains the correct number of bars with exact integer values matching the CSV content.

**Acceptance Scenarios**:

1. **Given** ingested CSV files for BTCUSDT from January 2024 to March 2024, **When** the candle loader is asked for data from `2024-01-15` to `2024-02-28`, **Then** it returns a `TimeSeries<IntBar>` containing only candles within that date range, loaded from the `2024-01.csv` and `2024-02.csv` partition files.

2. **Given** a CSV file with integer-encoded candle data, **When** the candle loader reads it, **Then** all values are parsed using `long.Parse()` with no floating-point operations involved.

3. **Given** a requested date range that spans a month with no data file, **When** the candle loader processes that range, **Then** it skips the missing month gracefully and returns data from the available months only.

---

### Edge Cases

- What happens when the exchange API returns an empty response for a valid time range (e.g., trading was halted)? The service logs a warning and stops fetching for that asset's current run — no infinite retry loop.
- What happens when the exchange API returns duplicate candles across pagination boundaries? The CSV writer's timestamp-based deduplication ensures no duplicate rows are written.
- What happens when a CSV partition file is corrupted or contains unparseable rows? The service logs an error and falls back to `HistoryStart` for that asset, triggering a full re-fetch.
- What happens when disk space runs out mid-write? The exception propagates and the service crashes. On restart, the orchestrator resumes from the last valid timestamp.
- What happens when the configured `DecimalDigits` changes for an existing asset? Existing data becomes inconsistent. This is a configuration error — the service should validate that `DecimalDigits` has not changed for assets with existing data, or log a critical warning.
- What happens when candle timestamps cross a month boundary within a single API response batch? The CSV writer routes each candle to its correct monthly partition file, switching files as needed.
- What happens when the exchange returns candles out of chronological order? The adapter is expected to return candles in ascending timestamp order (Binance guarantees this). If not, the CSV writer appends as received — a future integrity check could detect and correct this.
- What happens to existing tests and application code that reference the removed `Bar` / `IBarSource` types? All references are updated to use `IntBar` (with `long` OHLC) and the new `CandleLoader`-based data flow as part of this feature's breaking-change cleanup.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST fetch OHLCV candle data from exchange REST APIs in configurable time intervals (minimum 1-minute resolution)
- **FR-002**: System MUST convert all decimal price and volume values to integers using a configurable per-asset multiplier (`price * 10^DecimalDigits`) before storage
- **FR-003**: System MUST store candle data as CSV files partitioned by `{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv` with a header row and ascending timestamp order
- **FR-004**: System MUST detect the last stored candle timestamp and fetch only missing data on subsequent runs (incremental ingestion)
- **FR-005**: System MUST handle API pagination internally, streaming results to disk without buffering entire histories in memory
- **FR-006**: System MUST enforce exchange rate limits by tracking request frequency and adding configurable delays between API calls
- **FR-007**: System MUST write candle data idempotently — restarting after a crash must not produce duplicate rows
- **FR-008**: System MUST run as a long-lived background service with configurable scheduled execution (interval in hours) and optional run-on-startup
- **FR-009**: System MUST support configuring multiple assets, each with their own symbol, exchange, interval, decimal digits, and history start date. Asset configuration is part of the unified domain `Asset` model (extended with ingestion fields) rather than a separate configuration type
- **FR-010**: System MUST process assets on the same exchange sequentially to respect shared rate limits
- **FR-011**: System MUST provide a read-only candle loader that loads stored CSV data into `TimeSeries<IntBar>` for a given date range without floating-point conversion. This is the primary interface the backtest engine uses to consume candle data — the engine accepts `TimeSeries<IntBar>` directly rather than an async stream
- **FR-012**: System MUST handle API errors gracefully: retry with exponential backoff for rate limits and server errors, skip and continue for persistent failures
- **FR-013**: System MUST log structured events for ingestion start, batch progress, completion, and errors with contextual information (symbol, time range, duration, candle count)
- **FR-014**: System MUST support adapter-based exchange integration where each exchange has its own implementation that can be registered independently
- **FR-015**: System MUST create partition directories and files on demand when they do not yet exist
- **FR-016**: System MUST widen `IntBar` OHLC price fields from `int` to `long` to support the full range of integer-encoded values produced by the CSV writer, and update all dependent code (tests, metrics, strategies) accordingly
- **FR-017**: System MUST refactor the backtest engine to accept `TimeSeries<IntBar>` directly (loaded by CandleLoader) instead of consuming `IIntBarSource` async streams, and retire or deprecate `IIntBarSource` and `IBarSourceRepository`
- **FR-018**: System MUST extend the domain `Asset` record with exchange name, `DecimalDigits`, candle interval, and history start date fields, unifying trading and ingestion asset metadata into a single type
- **FR-019**: System MUST fix all broken `Bar` and `IBarSource` references across the codebase (tests, application layer, metrics) to use `IntBar` and the updated interfaces, resolving inconsistencies left from the prior `OhlcvBar` → `IntBar` refactor

### Key Entities

- **Raw Candle**: A candle as received from the exchange — timestamp (as `DateTimeOffset` UTC), open, high, low, close, volume as decimal values. Intermediate representation between API response and storage.
- **Integer Candle**: A candle with all values converted to integers via the decimal multiplier. The storage and backtest-consumption format.
- **Asset** (extended): The existing domain `Asset` record, extended with exchange name, `DecimalDigits` multiplier, candle interval, and history start date. Serves as the single source of truth for both trading characteristics and ingestion configuration.
- **Adapter Configuration**: Defines how to connect to an exchange — base URL, rate limit, and request delay settings.
- **Partition File**: A monthly CSV file containing integer candles for a single asset on a single exchange. Immutable once the month is complete; only the current month is appended to.

### Assumptions

- Binance public klines API does not require authentication for historical data access
- Exchange APIs return candles in ascending chronological order within each response
- The same `DecimalDigits` multiplier is applied to both price and volume fields for simplicity
- 1-minute candle resolution is sufficient for all initial use cases; higher timeframes can be aggregated from minute data by the backtest engine
- Monthly partitioning (up to ~44,640 rows per file at 1-minute resolution) provides an acceptable balance between file count and file size
- The service runs on a single machine and does not require distributed coordination
- Existing `TimeSeries<IntBar>` and `IntBar` types exist in the Domain layer; `IntBar` OHLC fields will be widened from `int` to `long` as part of this feature to support higher-precision multipliers and future price growth

## Clarifications

### Session 2026-02-09

- Q: How should the mismatch between IntBar's `int` OHLC fields and the design doc's `long` CSV values be handled? → A: Change `IntBar` to use `long` for OHLC fields (breaking change to existing domain type) for future-proofing.
- Q: How should CandleLoader integrate with the existing backtest engine interfaces (`IIntBarSource`, `IBarSourceRepository`)? → A: Replace `IIntBarSource` with `CandleLoader` directly. The backtest engine will accept `TimeSeries<IntBar>` instead of an async stream. This simplifies the read path — CandleLoader loads data into memory upfront and the engine iterates over it.
- Q: Should the ingestor use `DateTime` (per design doc) or `DateTimeOffset` (per existing domain types) for timestamps? → A: Use `DateTimeOffset` (UTC) everywhere. `RawCandle` and all internal types use `DateTimeOffset` to match existing domain conventions and avoid `DateTime.Kind` ambiguity.
- Q: Should ingestor `AssetOptions` and domain `Asset` be linked or separate? → A: Extend domain `Asset` with exchange and `DecimalDigits` fields so there's one source of truth for all asset metadata. The ingestor reads asset configuration from the unified `Asset` model rather than maintaining a parallel `AssetOptions` type.
- Q: Should broken `Bar`/`IBarSource` references in tests and application layer be cleaned up in this feature? → A: Yes, clean up as part of this feature. Fix all broken `Bar`/`IBarSource` references to use `IntBar` and the new interfaces, since we're already making breaking changes to `IntBar` and the backtest engine contract.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A full year of 1-minute candle data for a single asset (~525,000 candles) can be backfilled in a single run without manual intervention
- **SC-002**: Incremental ingestion runs complete within 2 minutes for assets that are less than 24 hours behind
- **SC-003**: Restarting the service at any point produces no duplicate candle rows in the CSV files
- **SC-004**: The candle loader can load one month of 1-minute data (~44,000 rows) into a `TimeSeries<IntBar>` in under 1 second
- **SC-005**: All ingestion runs produce structured log output sufficient to diagnose failures without accessing the CSV files directly
- **SC-006**: Adding a new asset to the configuration requires only a configuration change — no code modifications
- **SC-007**: The service recovers automatically from transient API failures (rate limits, server errors) without operator intervention
