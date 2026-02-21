# Plan: Backtest & Optimization Run Persistence Layer

## Context

Backtest and optimization results are ephemeral — computed and returned but never stored. The user wants a run history to observe strategy dynamics over time: comparing performance across parameter sets, asset/timeframe combinations, and strategy versions. This requires a persistence layer with SQLite, a repository abstraction, query endpoints, and a new strategy `Version` field.

**Storage choice**: SQLite now (zero deployment cost, file-based), with a clear migration path to PostgreSQL (as specified in the constitution for production). The `IRunRepository` interface abstracts the storage — swapping implementations requires changing one DI registration.

**Two databases coexist with different purposes**:

| Database | Purpose | Created by |
|----------|---------|------------|
| `trades.sqlite` | Debug/analysis artifact — order/trade lifecycle from JSONL events. No metrics, equity, or optimization grouping. | `IPostRunPipeline` (existing) |
| `runs.sqlite` (NEW) | Dashboard/report store — full `PerformanceMetrics`, equity curve with timestamps, optimization grouping, `run_folder_path` for linking to JSONL files. | `IRunRepository` (this feature) |

### Relationship to the debug feature

PR #6 introduced an event bus, JSONL event logging, post-run pipeline, `trades.sqlite`, `BacktestPreparer`, `IBacktestSetupCommand`, and refactored `BacktestEngine.Run()` to accept optional `IDebugProbe` + `IEventBus` parameters. This plan builds on those foundations:

- **`BacktestPreparer`** is the setup codepath — returns `BacktestSetup(Asset, ScaleFactor, Options, Strategy, Series)`. Handlers are thin orchestrators.
- **`BacktestEngine.Run(series, strategy, options, ct, probe?, bus?)`** — the `bus` parameter enables event emission. Normal backtests must pass `bus: eventBus` to emit candle/indicator/trade events for the frontend report charts.
- **`IStrategyFactory.Create(strategyName, indicatorFactory, parameters?)`** — normal backtests use `PassthroughIndicatorFactory.Instance`.
- **`IBacktestSetupCommand`** shared interface — implemented by both `RunBacktestCommand` and `StartDebugSessionCommand`. Any field additions must go through this interface.
- **`StrategyBase`** implements `IEventBusReceiver` — has `EventBus`, `Indicators`, `EmitSignal()`. The `Version` property addition (Phase 1) fits alongside these.
- **`RunIdentity` / `RunSummary` / `RunMeta`** — Application-layer records from the debug feature that capture run identity and summary. The backtest handler should build a `RunIdentity` (following the pattern in `StartDebugSessionCommandHandler`) and pass it to `IRunSinkFactory.Create()`.
- **`Microsoft.Data.Sqlite` 9.0.4** already in `Infrastructure.csproj` — no package addition needed.

## Scope Summary

1. **Strategy Version** — add `string Version` to `IStrategy`/`StrategyBase`, require concrete strategies to declare semver
2. **Equity timestamps** — pair equity curve values with bar timestamps in `BacktestEngine` / `BacktestResult`
3. **Normal backtests emit events** — wire `RunBacktestCommandHandler` to the JSONL event pipeline (same as debug handler)
4. **Persistence models** — Application-layer records for backtest runs (with equity curve, params, metrics, `run_folder_path`) and optimization runs (grouping child trials)
5. **Repository interface** — `IRunRepository` in Application with save/get/query methods, including `GetDistinctStrategyNamesAsync()` for the strategies endpoint
6. **SQLite implementation** — raw `Microsoft.Data.Sqlite` ADO.NET in Infrastructure (no EF Core)
7. **Handler integration** — `RunBacktestCommandHandler` and `RunOptimizationCommandHandler` persist results after computation
8. **Query endpoints** — GET/list endpoints aligned with the frontend dashboard/report API contract

---

## SQLite Schema

```sql
CREATE TABLE IF NOT EXISTS optimization_runs (
    id                  TEXT    NOT NULL PRIMARY KEY,
    strategy_name       TEXT    NOT NULL,
    strategy_version    TEXT    NOT NULL,
    started_at          TEXT    NOT NULL,
    completed_at        TEXT    NOT NULL,
    duration_ms         INTEGER NOT NULL,
    total_combinations  INTEGER NOT NULL,
    sort_by             TEXT    NOT NULL,
    data_start          TEXT    NOT NULL,
    data_end            TEXT    NOT NULL,
    initial_cash        TEXT    NOT NULL,
    commission          TEXT    NOT NULL,
    slippage_ticks      INTEGER NOT NULL,
    max_parallelism     INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS backtest_runs (
    id                  TEXT    NOT NULL PRIMARY KEY,
    strategy_name       TEXT    NOT NULL,
    strategy_version    TEXT    NOT NULL,
    parameters_json     TEXT    NOT NULL,
    initial_cash        TEXT    NOT NULL,
    commission          TEXT    NOT NULL,
    slippage_ticks      INTEGER NOT NULL,
    started_at          TEXT    NOT NULL,
    completed_at        TEXT    NOT NULL,
    data_start          TEXT    NOT NULL,
    data_end            TEXT    NOT NULL,
    duration_ms         INTEGER NOT NULL,
    total_bars          INTEGER NOT NULL,
    metrics_json        TEXT    NOT NULL,
    equity_curve_json   TEXT    NOT NULL,
    run_folder_path     TEXT    NULL,
    run_mode            TEXT    NOT NULL DEFAULT 'Backtest',
    optimization_run_id TEXT    NULL REFERENCES optimization_runs(id)
);

-- Normalized for searchable asset+TF queries on backtest runs
CREATE TABLE IF NOT EXISTS backtest_data_subscriptions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    backtest_run_id TEXT NOT NULL REFERENCES backtest_runs(id) ON DELETE CASCADE,
    asset_name      TEXT NOT NULL,
    exchange        TEXT NOT NULL,
    timeframe       TEXT NOT NULL
);

-- Normalized for searchable asset+TF queries on optimization runs
CREATE TABLE IF NOT EXISTS optimization_data_subscriptions (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    optimization_run_id TEXT NOT NULL REFERENCES optimization_runs(id) ON DELETE CASCADE,
    asset_name          TEXT NOT NULL,
    exchange            TEXT NOT NULL,
    timeframe           TEXT NOT NULL
);

-- Indexes
CREATE INDEX IF NOT EXISTS ix_br_strategy ON backtest_runs(strategy_name);
CREATE INDEX IF NOT EXISTS ix_br_completed ON backtest_runs(completed_at);
CREATE INDEX IF NOT EXISTS ix_br_opt_id ON backtest_runs(optimization_run_id);
CREATE INDEX IF NOT EXISTS ix_bds_asset ON backtest_data_subscriptions(asset_name, exchange);
CREATE INDEX IF NOT EXISTS ix_bds_tf ON backtest_data_subscriptions(timeframe);
CREATE INDEX IF NOT EXISTS ix_bds_run ON backtest_data_subscriptions(backtest_run_id);
CREATE INDEX IF NOT EXISTS ix_ods_asset ON optimization_data_subscriptions(asset_name, exchange);
CREATE INDEX IF NOT EXISTS ix_ods_run ON optimization_data_subscriptions(optimization_run_id);
```

### Design notes

- **`metrics_json`** stores full `PerformanceMetrics` (all 19 fields), not a subset. Constants are referenced via `MetricNames` (in `AlgoTradeForge.Domain.Reporting`) for compile-time safety.
- **`equity_curve_json`** stores `[{t:timestampMs, v:equityLong}]` — paired timestamps from the engine with portfolio equity values. This is the frontend equity chart's data source.
- **`run_folder_path`** (nullable) links to the JSONL event folder for candle/indicator/trade data. Null for optimization trial children (they don't emit events). Non-null for standalone backtests and debug runs.
- **`run_mode`** is `'Backtest'` or `'Debug'` — distinguishes how the run was initiated.
- **`optimization_data_subscriptions`** is a separate table because optimization runs can have multiple data subscriptions (the DataSubscriptions axis), independently from the per-trial backtest subscriptions.
- Data subscriptions are normalized (not JSON) for efficient WHERE/JOIN filtering. Params, metrics, and equity curve are JSON blobs — write-once, read-as-whole.
- Decimals stored as TEXT to preserve financial precision.
- Fills are NOT stored in `runs.sqlite` — they live in the JSONL event files at `run_folder_path`, accessible via the event log infrastructure.

---

## Implementation Phases

### Phase 1: Domain — Strategy Version + Equity Timestamps

**Modify** `src/AlgoTradeForge.Domain/Strategy/IStrategy.cs` — add `string Version { get; }`

**Modify** `src/AlgoTradeForge.Domain/Strategy/StrategyBase.cs` — add `public abstract string Version { get; }`

**Modify** `src/AlgoTradeForge.Domain/Strategy/ZigZagBreakout/ZigZagBreakoutStrategy.cs` — add `public override string Version => "1.0.0";`

**Modify** `src/AlgoTradeForge.Domain/Engine/BacktestEngine.cs`:
- Add `EquityTimestamps` list to `RunState` (alongside existing `EquityCurve` at line 481):
  ```csharp
  public readonly List<long> EquityTimestamps = [];
  ```
- In `RunMainLoop` at line 122, after `state.EquityCurve.Add(...)`, add:
  ```csharp
  state.EquityTimestamps.Add(minTimestampMs);
  ```
  The `minTimestampMs` is already computed at line 115.
- Update the `return` at line 46 to include `EquityTimestamps`:
  ```csharp
  return new BacktestResult(state.Portfolio, state.Fills, state.EquityCurve,
      state.EquityTimestamps, state.TotalBarsDelivered, stopwatch.Elapsed);
  ```

**Modify** `src/AlgoTradeForge.Domain/Engine/BacktestResult.cs` — add `IReadOnlyList<long> EquityTimestamps` field:
```csharp
public sealed record BacktestResult(
    Portfolio FinalPortfolio,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<long> EquityCurve,
    IReadOnlyList<long> EquityTimestamps,
    int TotalBarsProcessed,
    TimeSpan Duration);
```

**Modify** `src/AlgoTradeForge.Domain/Engine/TimeFrameFormatter.cs` — change `internal static class` to `public static class` (needed by persistence layer for timeframe serialization in `DataSubscriptionRecord`).

### Phase 2: Application — Persistence Models & Repository Interface

**Create** `src/AlgoTradeForge.Application/Persistence/RunStorageOptions.cs`
```csharp
public sealed record RunStorageOptions
{
    public static string DefaultDatabasePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge",
            "Data",
            "runs.sqlite");

    public string DatabasePath { get; init; } = DefaultDatabasePath;
}
```

**Create** `src/AlgoTradeForge.Application/Persistence/BacktestRunRecord.cs` — contains:
- `BacktestRunRecord` — Guid Id, strategy name/version, params dict, data subscriptions, initial cash/commission/slippage, timestamps, duration, total bars, `PerformanceMetrics` (reuse domain type directly — all primitives, JSON-serializable), equity curve as `IReadOnlyList<EquityPoint>`, nullable `RunFolderPath`, `RunMode`, nullable optimization run ID
- `EquityPoint(long TimestampMs, long Value)` — pairs equity snapshots with bar timestamps
- `DataSubscriptionRecord(string AssetName, string Exchange, string TimeFrame)` — normalized for filtering

**Create** `src/AlgoTradeForge.Application/Persistence/OptimizationRunRecord.cs` — Guid Id, strategy name/version, timestamps, duration, total combinations, sort by, `IReadOnlyList<DataSubscriptionRecord> DataSubscriptions`, initial cash, commission, slippage ticks, max parallelism, `IReadOnlyList<BacktestRunRecord> Trials`

**Create** `src/AlgoTradeForge.Application/Persistence/BacktestRunQuery.cs` — filter record with optional strategy name, asset, exchange, timeframe, date range, limit/offset. Plus `OptimizationRunQuery`.

**Create** `src/AlgoTradeForge.Application/Persistence/IRunRepository.cs`
```
SaveAsync(BacktestRunRecord)
GetByIdAsync(Guid) → BacktestRunRecord?
QueryAsync(BacktestRunQuery) → IReadOnlyList<BacktestRunRecord>
SaveOptimizationAsync(OptimizationRunRecord)
GetOptimizationByIdAsync(Guid) → OptimizationRunRecord?
QueryOptimizationsAsync(OptimizationRunQuery) → IReadOnlyList<OptimizationRunRecord>
GetDistinctStrategyNamesAsync() → IReadOnlyList<string>
```

Named `IRunRepository` (not `IBacktestRunRepository`) since it handles both backtest and optimization runs.

### Phase 3: Application — Normal Backtests Emit Events + Handler Modifications

**Modify** `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`:

Wire the event pipeline (same pattern as `StartDebugSessionCommandHandler` lines 28-48):

1. Build `RunIdentity` with `RunMode = ExportMode.Backtest`, `StrategyVersion = setup.Strategy.Version`
2. Create `IRunSink` via `runSinkFactory.Create(identity)` — creates run folder + `events.jsonl`
3. Create `EventBus(ExportMode.Backtest, [sink])` — no WebSocket sink (that's debug-only)
4. Pass `bus: eventBus` to `engine.Run()`
5. After run: build `RunSummary`, call `sink.WriteMeta(summary)`, call `postRunPipeline.Execute()`

New handler dependencies: `IRunSinkFactory`, `IPostRunPipeline`, `IRunRepository`.

After computing metrics:
- Build scaled `PerformanceMetrics` (using `with` to divide dollar fields by scaleFactor — same pattern optimization handler already uses)
- Map `DataSubscription` → `DataSubscriptionRecord` (using `TimeFrameFormatter.Format()`)
- Build `BacktestRunRecord` with `RunFolderPath = sink.RunFolderPath`, `RunMode = "Backtest"`
- Call `repository.SaveAsync(record)`

**Modify** `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs`:
- Add `IRunRepository` dependency
- Generate `optimizationRunId` before parallel loop
- In each trial, build a `BacktestRunRecord` with `OptimizationRunId` set, `RunFolderPath = null` (trials don't emit events), equity curve with timestamps
- After parallel loop, build `OptimizationRunRecord` with all trial records including `DataSubscriptions`, `InitialCash`, `Commission`, `SlippageTicks`, `SortBy`, `MaxParallelism`
- Call `repository.SaveOptimizationAsync(record)`

**Why optimization trials do NOT emit events**: Too expensive for thousands of trials. Their `RunFolderPath` is null in the persistence record. The optimization handler persists metrics + equity curve directly to `runs.sqlite` via `IRunRepository`.

**Create** query + handler files (handler is trivial delegation to repository, combined in one file per query):
- `src/AlgoTradeForge.Application/Backtests/GetBacktestByIdQuery.cs`
- `src/AlgoTradeForge.Application/Backtests/ListBacktestRunsQuery.cs`
- `src/AlgoTradeForge.Application/Optimization/GetOptimizationByIdQuery.cs`
- `src/AlgoTradeForge.Application/Optimization/ListOptimizationRunsQuery.cs`

**Modify** `src/AlgoTradeForge.Application/DependencyInjection.cs` — register 4 query handlers

### Phase 4: Infrastructure — SQLite Implementation

**Create** `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs` — static `EnsureCreatedAsync` with idempotent `CREATE TABLE IF NOT EXISTS` + `PRAGMA journal_mode=WAL`

**Create** `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs`:
- Primary ctor takes `IOptions<RunStorageOptions>`
- Lazy init via `SemaphoreSlim` + `_initialized` flag calling `EnsureCreatedAsync`
- `SaveAsync`: INSERT `backtest_runs` + N `backtest_data_subscriptions` in a transaction
- `SaveOptimizationAsync`: INSERT `optimization_runs` + N `optimization_data_subscriptions` + N `backtest_runs` (each with subscriptions) in a transaction
- `GetByIdAsync`: SELECT + JOIN to `backtest_data_subscriptions`, deserialize JSON columns
- `QueryAsync`: Dynamic WHERE clause, JOIN to subscriptions when filtering by asset/exchange/TF, ORDER BY `completed_at DESC`, LIMIT/OFFSET
- `GetDistinctStrategyNamesAsync`: SELECT DISTINCT `strategy_name` from `backtest_runs` UNION from `optimization_runs`
- JSON serialization via `System.Text.Json.JsonSerializer`
- Connection pooling note: use `Pooling=False` in tests per project convention

**Modify** `src/AlgoTradeForge.Infrastructure/DependencyInjection.cs` — register `SqliteRunRepository` as singleton

### Phase 5: WebApi — Endpoints, Contracts & Config

**Create** `src/AlgoTradeForge.WebApi/Endpoints/StrategyEndpoints.cs` — `GET /api/strategies` returning distinct strategy names

**Create** `src/AlgoTradeForge.WebApi/Contracts/RunContracts.cs` — response DTOs for list/detail endpoints:
- `BacktestRunResponse` — includes metrics as `Dictionary<string, object>` so the FE table can render dynamic metric columns, plus `hasCandleData: bool` (true when `RunFolderPath` is non-null)
- `EquityPointResponse(long TimestampMs, decimal Value)` — for the equity chart endpoint
- `OptimizationRunResponse` with nested trial list

**Modify** `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`:
- Wire existing `GET /{id:guid}` stub to `GetBacktestByIdQuery` handler
- Add `GET /` with query params: `strategyName`, `assetName`, `exchange`, `timeFrame`, `from`, `to`, `limit`, `offset`
- Add `GET /{id:guid}/equity` — returns equity curve with timestamps

**Modify** `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`:
- Add `GET /{id:guid}` for single optimization with all trials
- Add `GET /` with query params for listing

**Modify** `src/AlgoTradeForge.WebApi/Program.cs`:
- Add `builder.Services.Configure<RunStorageOptions>(builder.Configuration.GetSection("RunStorage"))`
- Map `StrategyEndpoints`

**Modify** `src/AlgoTradeForge.WebApi/appsettings.json` — add `"RunStorage": {}`

### Frontend API alignment

| Endpoint | FE Consumer | Data Source |
|----------|-------------|-------------|
| `GET /api/strategies` | Sidebar strategy list | `runs.sqlite` DISTINCT query |
| `GET /api/backtests?strategyName=&...` | Dashboard runs table | `runs.sqlite` query with filtering/sorting |
| `GET /api/backtests/{id}` | Report screen header | `runs.sqlite` by ID |
| `GET /api/backtests/{id}/equity` | Equity chart | `runs.sqlite equity_curve_json` |
| `GET /api/optimizations` | Dashboard optimization list | `runs.sqlite` query |
| `GET /api/optimizations/{id}` | Optimization report with trials | `runs.sqlite` with child records |

**Out of scope** (deferred to frontend feature): `GET /api/backtests/{id}/candles`, `/indicators`, `/trades` — these read from JSONL event files and belong with the frontend chart implementation.

The list endpoint returns metrics as `Dictionary<string, object>` (serialized from `PerformanceMetrics` via reflection or manual mapping) so the FE table can render dynamic metric columns.

### Phase 6: Tests

**Create** `tests/AlgoTradeForge.Infrastructure.Tests/Persistence/SqliteRunRepositoryTests.cs` (uses temp file SQLite DB with `Pooling=False` per project convention, `IDisposable` cleanup with `SqliteConnection.ClearAllPools()`):
- Save + GetById round-trips all fields (equity curve with timestamps, params, metrics, subscriptions)
- GetById returns null for non-existent
- Query filters: by strategy name, by asset+exchange, by timeframe, by date range
- Query pagination (limit/offset)
- Query chronological order (completed_at DESC)
- SaveOptimization persists parent + children + optimization data subscriptions
- GetOptimizationById returns with all trials
- GetDistinctStrategyNames returns unique names across backtests and optimizations

---

## File Summary

### New files (14)
| File | Purpose |
|------|---------|
| `Domain/Reporting/MetricNames.cs` | Compile-time-safe metric name constants via `nameof()` |
| `Application/Persistence/RunStorageOptions.cs` | Config record for `runs.sqlite` path |
| `Application/Persistence/BacktestRunRecord.cs` | Persistence models (`BacktestRunRecord`, `EquityPoint`, `DataSubscriptionRecord`) |
| `Application/Persistence/OptimizationRunRecord.cs` | Optimization group model with data subscriptions |
| `Application/Persistence/BacktestRunQuery.cs` | Query filter records |
| `Application/Persistence/IRunRepository.cs` | Repository interface |
| `Application/Backtests/GetBacktestByIdQuery.cs` | Query + handler |
| `Application/Backtests/ListBacktestRunsQuery.cs` | Query + handler |
| `Application/Optimization/GetOptimizationByIdQuery.cs` | Query + handler |
| `Application/Optimization/ListOptimizationRunsQuery.cs` | Query + handler |
| `Infrastructure/Persistence/SqliteDbInitializer.cs` | Schema creation |
| `Infrastructure/Persistence/SqliteRunRepository.cs` | SQLite implementation |
| `WebApi/Endpoints/StrategyEndpoints.cs` | Strategy list endpoint |
| `WebApi/Contracts/RunContracts.cs` | Response DTOs |

### Modified files (15)
| File | Change |
|------|--------|
| `Domain/Strategy/IStrategy.cs` | Add `Version` property |
| `Domain/Strategy/StrategyBase.cs` | Add `abstract Version` |
| `Domain/Strategy/ZigZagBreakout/ZigZagBreakoutStrategy.cs` | Add `Version => "1.0.0"` |
| `Domain/Engine/BacktestEngine.cs` | Add `EquityTimestamps` to `RunState` + `RunMainLoop` + return |
| `Domain/Engine/BacktestResult.cs` | Add `EquityTimestamps` field |
| `Domain/Engine/TimeFrameFormatter.cs` | `internal` → `public` |
| `Application/Backtests/RunBacktestCommandHandler.cs` | Wire event pipeline + persist run |
| `Application/Optimization/RunOptimizationCommandHandler.cs` | Persist optimization + trials |
| `Application/Optimization/RunOptimizationCommand.cs` | `MetricNames.Default` (already done) |
| `Application/DependencyInjection.cs` | Register query handlers |
| `Infrastructure/DependencyInjection.cs` | Register `SqliteRunRepository` |
| `WebApi/Endpoints/BacktestEndpoints.cs` | Wire GET + add list + equity endpoint |
| `WebApi/Endpoints/OptimizationEndpoints.cs` | Add GET + list endpoints |
| `WebApi/Contracts/RunOptimizationRequest.cs` | `MetricNames.Default` (already done) |
| `WebApi/Program.cs` + `appsettings.json` | Config binding + strategy endpoints |

### Test files (2)
| File | Purpose |
|------|---------|
| `Domain.Tests/Reporting/MetricNamesTests.cs` | Reflection test: every `PerformanceMetrics` property has a `MetricNames` constant (already done) |
| `Infrastructure.Tests/Persistence/SqliteRunRepositoryTests.cs` | Integration tests for SQLite repository |

---

## Verification

1. `dotnet build AlgoTradeForge.slnx` — all projects compile
2. `dotnet test` — all existing + new tests pass
3. Run WebApi, POST a backtest → verify `events.jsonl` created in `Data/EventLogs/` AND result persisted to `runs.sqlite`
4. `GET /api/backtests` → verify run appears in list with full metrics
5. `GET /api/backtests/{id}` → verify detail with metrics dict, params dict, `hasCandleData: true`
6. `GET /api/backtests/{id}/equity` → verify equity curve with timestamps
7. POST an optimization → verify optimization + trials persisted
8. `GET /api/optimizations/{id}` → verify parent with all trials
9. `GET /api/strategies` → verify strategy names returned
