# Implementation Plan: Candle Data History Ingestion Service

**Branch**: `002-candle-ingestor` | **Date**: 2026-02-09 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/002-candle-ingestor/spec.md`

## Summary

Build an automated candle data ingestion pipeline that fetches OHLCV data from exchange APIs (starting with Binance), converts to integer-encoded CSV files partitioned by month, and provides a `CandleLoader` for backtest consumption as `TimeSeries<IntBar>`. Includes breaking changes to widen `IntBar` to `long`, unify `Asset` with ingestion fields, refactor the backtest engine to accept `TimeSeries<IntBar>` directly, and clean up broken `Bar`/`IBarSource` references.

## Technical Context

**Language/Version**: C# 14 / .NET 10
**Primary Dependencies**: `Microsoft.Extensions.Hosting` (BackgroundService), `System.Text.Json` (Binance API parsing), `Serilog` (structured logging)
**Storage**: Flat-file CSV partitioned by `{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv`
**Testing**: xUnit + NSubstitute (per constitution)
**Target Platform**: Windows (dev), Linux (Hetzner production)
**Project Type**: Multi-project solution (clean architecture)
**Performance Goals**: ~600K candles/min fetch rate; <1s to load 44K rows from CSV
**Constraints**: Binance rate limit 1200 weight/min; monthly partition files ≤2.6MB each
**Scale/Scope**: 2 assets initially (BTCUSDT, ETHUSDT); ~525K candles/year per asset at 1-min resolution

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Strategy-as-Code | PASS | No strategy changes. Strategies still receive `TimeSeries<IntBar>` via host context. |
| II. Test-First | PASS | Tests written alongside each component. xUnit + NSubstitute per constitution. |
| III. Data Integrity & Provenance | PASS | Immutable completed partitions; gap detection & logging; UTC timestamps; source attribution via exchange/symbol path. Versioning deferred (corrections create new versions — out of MVP scope). |
| IV. Observability & Auditability | PASS | Serilog structured JSON logs; per-run/per-asset/per-batch events; heartbeat file for monitoring. |
| V. Separation of Concerns | PASS | Background job (CandleIngestor) separate from API (WebApi). Domain types in Domain, orchestration in Application, adapters/storage in Infrastructure. Jobs are idempotent and resumable. |
| VI. Simplicity & YAGNI | PASS with notes | No database, no GUI, no streaming. Flat CSV files. Single exchange MVP. See Complexity Tracking for the new worker project justification. |
| Background Jobs | PARTIAL | Constitution says "MUST use a job framework with persistence (Hangfire, Quartz.NET)". We use `PeriodicTimer` in a `BackgroundService` instead — simpler, no dependency, and the CSV files themselves serve as checkpoints for resumability. See Complexity Tracking. |
| Test Framework | PASS | xUnit + NSubstitute. No FluentAssertions. |
| Code Style | PASS | File-scoped namespaces, primary constructors, `required` properties, `ct` for CancellationToken, no XML comments unless non-obvious. |
| Async/Concurrency | PASS | `async`/`await` throughout, `CancellationToken` propagation, `IAsyncEnumerable<T>` for streaming adapter results. |

**Post-Phase 1 re-check**: All gates pass. The `BackgroundService` + `PeriodicTimer` approach is justified in Complexity Tracking as a simpler alternative to Hangfire/Quartz for a single periodic job.

## Project Structure

### Documentation (this feature)

```text
specs/002-candle-ingestor/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   ├── csv-schema.md    # CSV partition file format
│   └── adapter-interface.md  # IDataAdapter contract
└── tasks.md             # Phase 2 output (from /speckit.tasks)
```

### Source Code (repository root)

```text
src/
├── AlgoTradeForge.Domain/
│   ├── Asset.cs                          # MODIFIED: add Exchange, DecimalDigits, SmallestInterval, HistoryStart
│   ├── History/
│   │   ├── IntBar.cs                     # MODIFIED: int → long for OHLC
│   │   ├── RawCandle.cs                  # NEW: decimal OHLCV from exchange
│   │   ├── IDataAdapter.cs               # NEW: exchange adapter interface
│   │   ├── TimeSeries.cs                 # UNCHANGED
│   │   ├── IIntBarSource.cs              # DELETED (retired)
│   │   ├── DataSource.cs                 # RETAINED (IDataSource still useful)
│   │   ├── HistoryDataQuery.cs           # UNCHANGED
│   │   └── Metadata/
│   │       ├── SampleMetadata.cs         # UNCHANGED
│   │       └── IntBarMetadata.cs         # UNCHANGED
│   ├── Engine/
│   │   └── BacktestEngine.cs             # MODIFIED: accept TimeSeries<IntBar> instead of IIntBarSource
│   ├── Reporting/
│   │   ├── IMetricsCalculator.cs         # MODIFIED: Bar → IntBar
│   │   └── MetricsCalculator.cs          # MODIFIED: Bar → IntBar, adapt for long OHLC
│   └── Strategy/
│       └── IIntBarStrategy.cs            # UNCHANGED
│
├── AlgoTradeForge.Application/
│   ├── CandleIngestion/
│   │   ├── IngestionOrchestrator.cs      # NEW: main ingestion loop
│   │   └── CandleLoader.cs              # NEW: CSV → TimeSeries<IntBar>
│   ├── Backtests/
│   │   ├── RunBacktestCommand.cs         # MODIFIED: remove BarSourceName, add data loading
│   │   └── RunBacktestCommandHandler.cs  # MODIFIED: use CandleLoader instead of IBarSourceRepository
│   └── Repositories/
│       ├── IAssetRepository.cs           # MODIFIED: fix Asset import
│       └── IBarSourceRepository.cs       # DELETED (retired)
│
├── AlgoTradeForge.Infrastructure/
│   ├── CandleIngestion/
│   │   ├── BinanceAdapter.cs             # NEW: Binance klines API adapter
│   │   ├── CsvCandleWriter.cs            # NEW: integer CSV writer with partitioning
│   │   └── RateLimiter.cs                # NEW: sliding-window rate limiter
│   └── History/
│       └── HistoryContext.cs             # UNCHANGED
│
└── AlgoTradeForge.CandleIngestor/        # NEW PROJECT (worker service)
    ├── AlgoTradeForge.CandleIngestor.csproj
    ├── Program.cs                        # Host builder, DI wiring
    ├── IngestionWorker.cs                # BackgroundService with PeriodicTimer
    └── appsettings.json                  # CandleIngestor configuration

tests/
├── AlgoTradeForge.Domain.Tests/
│   ├── Engine/
│   │   └── BacktestEngineTests.cs        # MODIFIED: fix Bar → IntBar, update for new engine signature
│   ├── History/
│   │   └── IntBarTests.cs               # NEW: verify long fields
│   ├── TestUtilities/
│   │   └── TestBars.cs                  # MODIFIED: Bar → IntBar with integer values
│   └── Reporting/
│       └── MetricsCalculatorTests.cs     # MODIFIED: fix Bar → IntBar (if tests exist)
│
└── AlgoTradeForge.Infrastructure.Tests/  # NEW PROJECT
    ├── AlgoTradeForge.Infrastructure.Tests.csproj
    └── CandleIngestion/
        ├── BinanceAdapterTests.cs        # NEW: mock HTTP, verify pagination/parsing
        ├── CsvCandleWriterTests.cs       # NEW: verify partitioning, dedup, integer encoding
        └── CandleLoaderTests.cs          # NEW: verify CSV → TimeSeries round-trip
```

**Structure Decision**: Follows existing clean architecture with Domain → Application → Infrastructure layering. The new `CandleIngestor` worker project is a thin hosting shell referencing Application and Infrastructure. A new `Infrastructure.Tests` project is needed because no infrastructure tests exist yet.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| New `CandleIngestor` worker project (5th project) | Headless background service with its own config, DI, and lifecycle. Cannot share process with WebApi due to different deployment targets. | Hosting in WebApi would violate Separation of Concerns (Principle V) — background ingestion shouldn't couple to the API lifecycle. |
| `PeriodicTimer` instead of Hangfire/Quartz | Single periodic job with CSV-based checkpointing. Adding Hangfire requires a database for job persistence, which contradicts the "no database" design goal. | Hangfire/Quartz would add unnecessary complexity and a database dependency for a single recurring task. The CSV files themselves provide checkpoint/resumability. |
| New `Infrastructure.Tests` project | Testing BinanceAdapter, CsvCandleWriter, and CandleLoader requires mocking HTTP and filesystem. | Putting infra tests in Domain.Tests would create incorrect project references and violate test isolation. |
