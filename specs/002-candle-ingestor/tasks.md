# Tasks: Candle Data History Ingestion Service

**Input**: Design documents from `/specs/002-candle-ingestor/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/

**Tests**: Tests are included — the constitution mandates Test-First (Principle II) and the spec includes FR-016/FR-019 breaking changes that require test updates.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Multi-project .NET solution**: `src/AlgoTradeForge.{Layer}/`, `tests/AlgoTradeForge.{Layer}.Tests/`
- Solution file: `AlgoTradeForge.slnx`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create new projects, update solution, install packages

- [x] T001 Create `AlgoTradeForge.CandleIngestor` worker project via `dotnet new worker` in `src/AlgoTradeForge.CandleIngestor/`. Add project references to Application and Infrastructure. Add NuGet packages: `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`. Target `net10.0` with `LangVersion 14`.
- [x] T002 Create `AlgoTradeForge.Infrastructure.Tests` test project via `dotnet new xunit` in `tests/AlgoTradeForge.Infrastructure.Tests/`. Add project reference to Infrastructure, Application, and Domain. Add NuGet packages: `NSubstitute`, `Microsoft.NET.Test.Sdk`. Target `net10.0` with `LangVersion 14`.
- [x] T003 Add both new projects to `AlgoTradeForge.slnx`. Run `dotnet build AlgoTradeForge.slnx` to verify solution compiles (expect broken `Bar`/`IBarSource` references — that's Phase 2).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain model changes and broken reference cleanup that MUST complete before any user story work

**CRITICAL**: No user story work can begin until this phase is complete. These are breaking changes to existing types used across the codebase.

### Domain Model Updates

- [x] T004 Widen `IntBar` OHLC fields from `int` to `long` in `src/AlgoTradeForge.Domain/History/IntBar.cs`. Change the record struct to: `IntBar(long Open, long High, long Low, long Close, long Volume)`.
- [x] T005 [P] Extend `Asset` record in `src/AlgoTradeForge.Domain/Asset.cs` with new properties: `Exchange` (string?, default null), `DecimalDigits` (int, default 2), `SmallestInterval` (TimeSpan, default 1 min), `HistoryStart` (DateOnly?, default null). Add a `Crypto` static factory method. Ensure existing `Equity()` and `Future()` factories still compile.
- [x] T006 [P] Create `RawCandle` readonly record struct in `src/AlgoTradeForge.Domain/History/RawCandle.cs` with fields: `DateTimeOffset Timestamp`, `decimal Open`, `decimal High`, `decimal Low`, `decimal Close`, `decimal Volume`.
- [x] T007 [P] Create `IDataAdapter` interface in `src/AlgoTradeForge.Domain/History/IDataAdapter.cs` per the adapter-interface contract: single method `FetchCandlesAsync(string symbol, TimeSpan interval, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)` returning `IAsyncEnumerable<RawCandle>`.
- [x] T008 [P] Create configuration records in `src/AlgoTradeForge.Domain/History/CandleIngestorOptions.cs`: `CandleIngestorOptions` (DataRoot, ScheduleIntervalHours, RunOnStartup, Adapters dict, Assets list), `AdapterOptions` (Type, BaseUrl, RateLimitPerMinute, RequestDelayMs), `IngestorAssetConfig` (Symbol, Exchange, SmallestInterval, DecimalDigits, HistoryStart). All per data-model.md.

### Broken Reference Cleanup (FR-019)

- [x] T009 Delete `src/AlgoTradeForge.Domain/History/IIntBarSource.cs` (retired per research R-002).
- [x] T010 [P] Fix `IMetricsCalculator` in `src/AlgoTradeForge.Domain/Reporting/IMetricsCalculator.cs`: replace `IReadOnlyList<Bar>` parameter with `IReadOnlyList<IntBar>`. Update the `using` to reference `AlgoTradeForge.Domain.History`.
- [x] T011 Fix `MetricsCalculator` in `src/AlgoTradeForge.Domain/Reporting/MetricsCalculator.cs`: replace all `Bar` references with `IntBar`. Adapt price access for `long` OHLC fields (e.g., cast to `decimal` for ratio calculations, or use `double` arithmetic). Ensure `BuildEquityCurve` and `Calculate` work with `IntBar` and `long` values.
- [x] T012 Refactor `BacktestEngine.RunAsync` in `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs`: change signature to accept `TimeSeries<IntBar> bars` instead of `IIntBarSource source`. Remove the `await foreach` over `source.GetBarsAsync()` and iterate `TimeSeries<IntBar>` directly. Update constructor to remove `IIntBarSource` dependency if present.
- [x] T013 Delete `src/AlgoTradeForge.Application/Repositories/IBarSourceRepository.cs` (retired).
- [x] T014 [P] Fix `IAssetRepository` in `src/AlgoTradeForge.Application/Repositories/IAssetRepository.cs`: ensure it imports `AlgoTradeForge.Domain` (not `AlgoTradeForge.Domain.Trading`) for the `Asset` type.
- [x] T015 Refactor `RunBacktestCommand` in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommand.cs`: remove `BarSourceName` property (no longer needed since CandleLoader replaces bar source lookup).
- [x] T016 Refactor `RunBacktestCommandHandler` in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`: remove `IBarSourceRepository` dependency from constructor. Update `HandleAsync` to use `CandleLoader` (placeholder call for now — full implementation in US4). Pass `TimeSeries<IntBar>` to `BacktestEngine.RunAsync`.

### Test Cleanup

- [x] T017 Rewrite `TestBars` in `tests/AlgoTradeForge.Domain.Tests/TestUtilities/TestBars.cs`: replace all `Bar` references with `IntBar`. Change factory methods to produce `IntBar` with `long` values (e.g., `Create(long open, long high, long low, long close, long volume)`). Remove `DateTimeOffset` from the factory — `IntBar` has no timestamp. Update `CreateSequence` accordingly.
- [x] T018 Rewrite `BacktestEngineTests` in `tests/AlgoTradeForge.Domain.Tests/Engine/BacktestEngineTests.cs`: replace `Bar`/`IBarSource` with `IntBar`/`TimeSeries<IntBar>`. Update `CreateBarSource` to create `TimeSeries<IntBar>` instead. Update all test methods to match the new `RunAsync(TimeSeries<IntBar>, ...)` signature. Verify tests compile and pass.
- [x] T019 [P] Create `IntBarTests` in `tests/AlgoTradeForge.Domain.Tests/History/IntBarTests.cs`: verify `IntBar` can hold `long` values beyond `int.MaxValue`, verify record struct equality, verify Volume is `long`.

**Checkpoint**: `dotnet build AlgoTradeForge.slnx` compiles with zero errors. `dotnet test` passes for Domain.Tests. All broken `Bar`/`IBarSource` references are resolved.

---

## Phase 3: User Story 1 — Initial Historical Backfill (Priority: P1) MVP

**Goal**: Fetch candle data from Binance API, convert to integers, write to partitioned CSV files. A single run produces a complete history from `HistoryStart` to now.

**Independent Test**: Configure one asset (BTCUSDT), run the service once, verify integer CSV files appear in `Data/Candles/Binance/BTCUSDT/{Year}/{YYYY-MM}.csv`.

### Tests for User Story 1

- [x] T020 [P] [US1] Create `CsvCandleWriterTests` in `tests/AlgoTradeForge.Infrastructure.Tests/CandleIngestion/CsvCandleWriterTests.cs`. Test: writing candles creates correct directory structure; header row written for new files; integer conversion is correct (decimal 67432.15 with DecimalDigits=2 → 6743215); month boundary routing writes to correct partition files; append mode doesn't duplicate header.
- [x] T021 [P] [US1] Create `BinanceAdapterTests` in `tests/AlgoTradeForge.Infrastructure.Tests/CandleIngestion/BinanceAdapterTests.cs`. Test with mocked `HttpClient` using `HttpMessageHandler`: parses Binance klines JSON response correctly; paginates when response has 1000 candles (advances startTime); yields empty on empty response; maps interval TimeSpan to Binance string format ("1m", "5m", "1h").

### Implementation for User Story 1

- [x] T022 [P] [US1] Implement `RateLimiter` in `src/AlgoTradeForge.Infrastructure/CandleIngestion/RateLimiter.cs`. Sliding-window rate limiter that tracks request timestamps, enforces `RateLimitPerMinute`, and adds `RequestDelayMs` between requests. Expose `async Task WaitAsync(CancellationToken ct)` method.
- [x] T023 [US1] Implement `BinanceAdapter : IDataAdapter` in `src/AlgoTradeForge.Infrastructure/CandleIngestion/BinanceAdapter.cs`. Inject `HttpClient` and `AdapterOptions`. Implement `FetchCandlesAsync` per adapter-interface contract: call `/api/v3/klines`, parse `JsonElement[]` response, yield `RawCandle` with `DateTimeOffset` timestamps, paginate by advancing `startTime`. Use `RateLimiter` for throttling. Handle HTTP 429 (exponential backoff), 418 (throw critical), 5xx (retry 3x). Use `[EnumeratorCancellation]` attribute on `ct`.
- [x] T024 [US1] Implement `CsvCandleWriter` in `src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvCandleWriter.cs`. Accept `RawCandle` + `int decimalDigits` + partition path info. Convert decimal→long via `(long)Math.Round(value * (decimal)Math.Pow(10, decimalDigits), MidpointRounding.AwayFromZero)`. Route to `{DataRoot}/{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv`. Create directories on demand. Write header for new files. Append data rows for existing files. Maintain open `StreamWriter` for current partition, switch on month boundary. Flush after each batch.
- [x] T025 [US1] Implement `IngestionOrchestrator` in `src/AlgoTradeForge.Application/CandleIngestion/IngestionOrchestrator.cs`. Inject `IServiceProvider` (for keyed `IDataAdapter` resolution), `CsvCandleWriter`, `IOptions<CandleIngestorOptions>`, `ILogger`. Method `RunAsync(CancellationToken ct)`: iterate configured assets, resolve adapter by exchange name, determine fetch range (HistoryStart to UtcNow for first run), stream candles from adapter, pass to writer. Process assets on same exchange sequentially. Log structured events per FR-013: `IngestionStarted`, `AssetIngestionStarted`, `BatchFetched`, `AssetIngestionCompleted`, `IngestionCompleted`.
- [x] T026 [US1] Write `appsettings.json` in `src/AlgoTradeForge.CandleIngestor/appsettings.json` with the `CandleIngestor` config section per research R-008. Include Binance adapter config and BTCUSDT + ETHUSDT assets with `DecimalDigits: 2`, `HistoryStart: 2024-01-01`, `SmallestInterval: 00:01:00`.
- [x] T027 [US1] Wire up DI in `src/AlgoTradeForge.CandleIngestor/Program.cs`. Bind `CandleIngestorOptions` from config. Register `HttpClient` for `BinanceAdapter`. Register `BinanceAdapter` as keyed singleton `IDataAdapter("Binance")`. Register `CsvCandleWriter` and `IngestionOrchestrator` as singletons. Configure Serilog with console + rolling file sinks. Do NOT register `IngestionWorker` yet (that's US3).

**Checkpoint**: `dotnet build` succeeds. Unit tests for CSV writer and Binance adapter pass. Can invoke `IngestionOrchestrator.RunAsync()` programmatically from a test or temporary `Main()` and see CSV files written to disk.

---

## Phase 4: User Story 2 — Incremental Catch-Up Ingestion (Priority: P2)

**Goal**: Detect last stored timestamp, fetch only missing candles, append without duplicates. Safe to restart at any point.

**Independent Test**: Run ingestion once (US1), note last timestamp, run again, verify only new candles appended and no duplicates exist.

### Tests for User Story 2

- [x] T028 [P] [US2] Add incremental ingestion tests to `tests/AlgoTradeForge.Infrastructure.Tests/CandleIngestion/CsvCandleWriterTests.cs`: test `GetLastTimestamp` reads last line of latest partition; test that writing a candle with timestamp <= last timestamp is skipped (dedup); test that appending to existing file produces no duplicate header row.

### Implementation for User Story 2

- [x] T029 [US2] Add `GetLastTimestamp` method to `CsvCandleWriter` in `src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvCandleWriter.cs`. Given exchange/symbol/dataRoot, find the latest partition file (sort by year/month), read its last line, parse the timestamp. Return `DateTimeOffset?` (null if no data exists).
- [x] T030 [US2] Update `IngestionOrchestrator.RunAsync` in `src/AlgoTradeForge.Application/CandleIngestion/IngestionOrchestrator.cs` to call `GetLastTimestamp` before fetching. If data exists: set `fetchFrom = lastTimestamp + interval`. If `fetchFrom >= UtcNow`: skip (already up to date). Log gap warning if `fetchFrom` is more than `2 * interval` behind. Handle corrupt CSV fallback to `HistoryStart` with error log.
- [x] T031 [US2] Add timestamp-based deduplication to `CsvCandleWriter.WriteCandle` in `src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvCandleWriter.cs`. Before appending, compare incoming candle timestamp against the last written timestamp. Skip if incoming <= last (makes writes idempotent).

**Checkpoint**: Run ingestion twice in succession — second run fetches only new candles. Verify CSV files have no duplicates. Restart mid-run and verify recovery works.

---

## Phase 5: User Story 3 — Scheduled Automated Ingestion (Priority: P3)

**Goal**: The service runs as a long-lived `BackgroundService` with a `PeriodicTimer`, executing ingestion on a configurable schedule.

**Independent Test**: Start the service with a short interval, verify it runs ingestion on startup (if configured) and repeats at the interval.

### Implementation for User Story 3

- [x] T032 [US3] Implement `IngestionWorker` as a `BackgroundService` in `src/AlgoTradeForge.CandleIngestor/IngestionWorker.cs`. Inject `IngestionOrchestrator`, `IOptions<CandleIngestorOptions>`, `ILogger<IngestionWorker>`. In `ExecuteAsync`: if `RunOnStartup`, call `orchestrator.RunAsync(ct)`. Then loop with `PeriodicTimer(TimeSpan.FromHours(options.Value.ScheduleIntervalHours))`. Wrap each run in try/catch — log errors but don't crash the timer loop. Respect `CancellationToken` for graceful shutdown.
- [x] T033 [US3] Register `IngestionWorker` as a hosted service in `src/AlgoTradeForge.CandleIngestor/Program.cs` via `builder.Services.AddHostedService<IngestionWorker>()`. Add `await host.RunAsync()` at the end.
- [x] T034 [US3] Add heartbeat file write to `IngestionOrchestrator` in `src/AlgoTradeForge.Application/CandleIngestion/IngestionOrchestrator.cs`. After each successful run, write current UTC timestamp to `{DataRoot}/candle-ingestor-heartbeat.txt`.

**Checkpoint**: `dotnet run --project src/AlgoTradeForge.CandleIngestor` starts the service. It runs ingestion on startup, then repeats on schedule. Ctrl+C shuts it down gracefully. Heartbeat file is updated.

---

## Phase 6: User Story 4 — Candle Data Loading for Backtest Consumption (Priority: P4)

**Goal**: Provide `CandleLoader` that reads partitioned CSV files into `TimeSeries<IntBar>` for a given date range, completing the end-to-end pipeline from ingestion to backtest.

**Independent Test**: Load a known CSV file via CandleLoader, verify `TimeSeries<IntBar>` contains correct bar count and exact integer values.

### Tests for User Story 4

- [x] T035 [P] [US4] Create `CandleLoaderTests` in `tests/AlgoTradeForge.Infrastructure.Tests/CandleIngestion/CandleLoaderTests.cs`. Test: loads single month CSV into `TimeSeries<IntBar>` with correct count and values; loads multi-month range spanning year boundary; filters rows outside requested date range; skips missing month files gracefully; returns empty series for range with no data; uses `long.Parse()` not float parsing (verify via known test data).

### Implementation for User Story 4

- [x] T036 [US4] Implement `CandleLoader` in `src/AlgoTradeForge.Application/CandleIngestion/CandleLoader.cs`. Static method `Load(string dataRoot, string exchange, string symbol, int decimalDigits, DateOnly from, DateOnly to, TimeSpan interval)` returning `TimeSeries<IntBar>`. Enumerate monthly partition files in `[from, to]` range. For each file: stream-parse CSV rows via `ReadLine()` + `Split(',')` + `long.Parse()`. Skip header row. Filter rows outside exact date range. Construct `TimeSeries<IntBar>` with correct `startTime` and `step`. Also implement `GetLastTimestamp(string dataRoot, string exchange, string symbol)` returning `DateTimeOffset?` — read last line of latest partition file.
- [x] T037 [US4] Update `RunBacktestCommandHandler` in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs` to use `CandleLoader.Load()` to get `TimeSeries<IntBar>` and pass it to `BacktestEngine.RunAsync()`. Replace the placeholder from T016 with the actual `CandleLoader` call using asset config (exchange, symbol, decimalDigits, date range).
- [x] T038 [US4] Refactor `CsvCandleWriter.GetLastTimestamp` (from T029) to delegate to `CandleLoader.GetLastTimestamp` in `src/AlgoTradeForge.Infrastructure/CandleIngestion/CsvCandleWriter.cs` and `src/AlgoTradeForge.Application/CandleIngestion/CandleLoader.cs`. This avoids duplicating the "read last line of CSV" logic — the writer calls the loader's static method.

**Checkpoint**: Write CSV test data → load via `CandleLoader` → verify `TimeSeries<IntBar>` round-trip. `RunBacktestCommandHandler` compiles with CandleLoader integration. Full pipeline: Ingest → Store → Load → Backtest engine.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, build verification, and cleanup

- [x] T039 Run `dotnet build AlgoTradeForge.slnx` and verify zero warnings, zero errors across all projects
- [x] T040 Run `dotnet test AlgoTradeForge.slnx` and verify all tests pass (Domain.Tests + Infrastructure.Tests)
- [ ] T041 [P] Verify Serilog structured logging output in `src/AlgoTradeForge.CandleIngestor/` — run the service briefly and confirm JSON log entries appear for IngestionStarted, AssetIngestionStarted, BatchFetched, AssetIngestionCompleted, IngestionCompleted events
- [x] T042 [P] Run `dotnet format AlgoTradeForge.slnx --verify-no-changes` to check code style compliance. Fix any formatting issues.
- [ ] T043 Run quickstart.md validation: follow the steps in `specs/002-candle-ingestor/quickstart.md` end-to-end and verify the documented workflow works as described

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 (projects must exist to modify) — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Phase 2 completion — provides the write path
- **US2 (Phase 4)**: Depends on US1 (needs CSV writer and orchestrator to add incremental logic)
- **US3 (Phase 5)**: Depends on US1 (needs orchestrator to wrap in scheduled service)
- **US4 (Phase 6)**: Depends on US1 (needs CSV files to exist for loading); can run in parallel with US2/US3
- **Polish (Phase 7)**: Depends on all user stories being complete

### User Story Dependencies

- **US1 (P1 — MVP)**: Can start after Phase 2. No dependencies on other stories.
- **US2 (P2)**: Extends US1 components (orchestrator, writer). Must follow US1.
- **US3 (P3)**: Wraps US1 orchestrator in BackgroundService. Can follow US1 directly (parallel with US2).
- **US4 (P4)**: Read-side loader. Can start after Phase 2 if test CSV data is provided manually, or after US1 for real data. Can run in parallel with US2/US3.

### Within Each User Story

- Tests FIRST (write and verify they fail)
- Infrastructure/models before services
- Services before orchestration/integration
- Core implementation before wiring/config

### Parallel Opportunities

**Phase 2** (after T004 IntBar widening):
- T005, T006, T007, T008 can all run in parallel (different files)
- T010, T014 can run in parallel (different files)
- T017, T019 can run in parallel (different test files)

**Phase 3** (US1):
- T020, T021 (test files) can run in parallel
- T022 (RateLimiter) can run in parallel with T020, T021

**After US1 is complete**:
- US3 (Phase 5) and US4 (Phase 6) can run in parallel with US2 (Phase 4)

---

## Parallel Example: Phase 2 Foundational

```
# Launch domain type changes in parallel:
Task: T005 "Extend Asset record in src/AlgoTradeForge.Domain/Asset.cs"
Task: T006 "Create RawCandle in src/AlgoTradeForge.Domain/History/RawCandle.cs"
Task: T007 "Create IDataAdapter in src/AlgoTradeForge.Domain/History/IDataAdapter.cs"
Task: T008 "Create config records in src/AlgoTradeForge.Domain/History/CandleIngestorOptions.cs"

# Then after broken reference cleanup completes, launch test updates in parallel:
Task: T017 "Rewrite TestBars in tests/.../TestUtilities/TestBars.cs"
Task: T019 "Create IntBarTests in tests/.../History/IntBarTests.cs"
```

## Parallel Example: User Story 1

```
# Launch US1 tests in parallel:
Task: T020 "CsvCandleWriterTests in tests/.../CandleIngestion/CsvCandleWriterTests.cs"
Task: T021 "BinanceAdapterTests in tests/.../CandleIngestion/BinanceAdapterTests.cs"

# RateLimiter has no dependencies on other US1 tasks:
Task: T022 "RateLimiter in src/.../CandleIngestion/RateLimiter.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001-T003)
2. Complete Phase 2: Foundational (T004-T019) — **CRITICAL, blocks everything**
3. Complete Phase 3: User Story 1 (T020-T027)
4. **STOP and VALIDATE**: Build compiles, tests pass, CSV files appear on disk
5. At this point you have a working ingestion pipeline invokable programmatically

### Incremental Delivery

1. Setup + Foundational → Codebase compiles cleanly with `IntBar(long)`, `Asset` extended, broken refs fixed
2. Add US1 → Can fetch and store candle history via code → **MVP**
3. Add US2 → Incremental catch-up, idempotent restarts
4. Add US3 → Fully automated scheduled service (`dotnet run`)
5. Add US4 → End-to-end pipeline: Ingest → Store → Load → Backtest
6. Polish → Build/test/format verification

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks in same phase
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable after Phase 2
- Commit after each task or logical group
- Stop at any checkpoint to validate independently
- The spec requires test-first per constitution Principle II — write tests before implementation within each story
