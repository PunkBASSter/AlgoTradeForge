# Plan: Backtest & Optimization Run Persistence Layer

## Context

Backtest and optimization results are ephemeral — they're computed and returned but never stored. The user wants a run history to observe strategy dynamics over time: comparing performance across parameter sets, asset/timeframe combinations, and strategy versions. This requires a persistence layer with SQLite, a repository abstraction, query endpoints, and a new strategy `Version` field.

**Storage choice**: SQLite now (zero deployment cost, file-based), with a clear migration path to PostgreSQL (as specified in the constitution for production). The `IBacktestRunRepository` interface abstracts the storage — swapping implementations requires changing one DI registration.

## Scope Summary

1. **Strategy Version** — add `string Version` to `IStrategy`/`StrategyBase`, require concrete strategies to declare semver
2. **Persistence models** — Application-layer records for backtest runs (with fills, equity curve, params, metrics) and optimization runs (grouping child trials)
3. **Repository interface** — `IBacktestRunRepository` in Application with save/get/query methods
4. **SQLite implementation** — raw `Microsoft.Data.Sqlite` ADO.NET in Infrastructure (no EF Core)
5. **Handler integration** — `RunBacktestCommandHandler` and `RunOptimizationCommandHandler` persist results after computation
6. **Query endpoints** — GET/list endpoints for backtest runs and optimization runs with filtering

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
    sort_by             TEXT    NOT NULL
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
    fills_json          TEXT    NOT NULL,
    equity_curve_json   TEXT    NOT NULL,
    optimization_run_id TEXT    NULL REFERENCES optimization_runs(id)
);

-- Normalized for searchable asset+TF queries
CREATE TABLE IF NOT EXISTS backtest_data_subscriptions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    backtest_run_id TEXT NOT NULL REFERENCES backtest_runs(id) ON DELETE CASCADE,
    asset_name      TEXT NOT NULL,
    exchange        TEXT NOT NULL,
    timeframe       TEXT NOT NULL
);

-- Indexes
CREATE INDEX IF NOT EXISTS ix_br_strategy ON backtest_runs(strategy_name);
CREATE INDEX IF NOT EXISTS ix_br_completed ON backtest_runs(completed_at);
CREATE INDEX IF NOT EXISTS ix_br_opt_id ON backtest_runs(optimization_run_id);
CREATE INDEX IF NOT EXISTS ix_bds_asset ON backtest_data_subscriptions(asset_name, exchange);
CREATE INDEX IF NOT EXISTS ix_bds_tf ON backtest_data_subscriptions(timeframe);
CREATE INDEX IF NOT EXISTS ix_bds_run ON backtest_data_subscriptions(backtest_run_id);
```

**Design notes**: Data subscriptions are normalized (not JSON) for efficient WHERE/JOIN filtering. Fills, equity curve, params, and metrics are JSON blobs — write-once, read-as-whole. Decimals stored as TEXT to preserve financial precision.

---

## Implementation Phases

### Phase 1: Domain — Strategy Version

**Modify** `src/AlgoTradeForge.Domain/Strategy/IStrategy.cs` — add `string Version { get; }`
**Modify** `src/AlgoTradeForge.Domain/Strategy/StrategyBase.cs` — add `public abstract string Version { get; }`
**Modify** `src/AlgoTradeForge.Domain/Strategy/ZigZagBreakout/ZigZagBreakoutStrategy.cs` — add `public override string Version => "1.0.0";`

### Phase 2: Application — Persistence Models & Repository Interface

**Create** `src/AlgoTradeForge.Application/Persistence/PersistenceOptions.cs`
```csharp
sealed record PersistenceOptions { string DatabasePath (defaults to LocalAppData/AlgoTradeForge/algotradeforge.db) }
```

**Create** `src/AlgoTradeForge.Application/Persistence/BacktestRunRecord.cs` — contains:
- `BacktestRunRecord` — Guid Id, strategy name/version, params dict, data subscriptions, initial cash/commission/slippage, timestamps, duration, total bars, `PerformanceMetrics` (reuse domain type directly), fills as `IReadOnlyList<FillRecord>`, equity curve as `IReadOnlyList<long>`, nullable optimization run ID
- `DataSubscriptionRecord` — asset name, exchange, timeframe (strings)
- `FillRecord` — order id, asset name, timestamp, decimal price, decimal quantity, string side, decimal commission (flattened from domain `Fill` which has `Asset` object + `long Price`)

**Create** `src/AlgoTradeForge.Application/Persistence/OptimizationRunRecord.cs` — Guid Id, strategy name/version, timestamps, duration, total combinations, sort by, `IReadOnlyList<BacktestRunRecord> Trials`

**Create** `src/AlgoTradeForge.Application/Persistence/BacktestRunQuery.cs` — filter record with optional strategy name, asset, exchange, timeframe, date range, limit/offset. Plus `OptimizationRunQuery`.

**Create** `src/AlgoTradeForge.Application/Persistence/IBacktestRunRepository.cs`
```
SaveAsync(BacktestRunRecord)
GetByIdAsync(Guid) → BacktestRunRecord?
QueryAsync(BacktestRunQuery) → IReadOnlyList<BacktestRunRecord>
SaveOptimizationAsync(OptimizationRunRecord)
GetOptimizationByIdAsync(Guid) → OptimizationRunRecord?
QueryOptimizationsAsync(OptimizationRunQuery) → IReadOnlyList<OptimizationRunRecord>
```

### Phase 3: Application — CQRS Query Types + Handler Modifications

**Create** query + handler files (handler is trivial delegation to repository, combined in one file per query):
- `src/AlgoTradeForge.Application/Backtests/GetBacktestByIdQuery.cs`
- `src/AlgoTradeForge.Application/Backtests/ListBacktestRunsQuery.cs`
- `src/AlgoTradeForge.Application/Optimization/GetOptimizationByIdQuery.cs`
- `src/AlgoTradeForge.Application/Optimization/ListOptimizationRunsQuery.cs`

**Modify** `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`:
- Add `IBacktestRunRepository` dependency
- After computing metrics, build scaled `PerformanceMetrics` (using `with` to divide dollar fields by scaleFactor — same pattern optimization handler already uses)
- Map domain `Fill` (long Price, Asset object) → `FillRecord` (decimal Price, string AssetName)
- Map `DataSubscription` → `DataSubscriptionRecord`
- Build `BacktestRunRecord` and call `SaveAsync`

**Modify** `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs`:
- Add `IBacktestRunRepository` dependency
- Generate `optimizationRunId` before parallel loop
- In each trial, build a `BacktestRunRecord` with `OptimizationRunId` set
- After parallel loop, build `OptimizationRunRecord` with all trial records, call `SaveOptimizationAsync`

**Modify** `src/AlgoTradeForge.Application/DependencyInjection.cs` — register 4 query handlers

### Phase 4: Infrastructure — SQLite Implementation

**Modify** `src/AlgoTradeForge.Infrastructure/AlgoTradeForge.Infrastructure.csproj` — add `Microsoft.Data.Sqlite` package

**Create** `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs` — static `EnsureCreatedAsync` with idempotent `CREATE TABLE IF NOT EXISTS` + `PRAGMA journal_mode=WAL`

**Create** `src/AlgoTradeForge.Infrastructure/Persistence/SqliteBacktestRunRepository.cs`:
- Primary ctor takes `IOptions<PersistenceOptions>`
- Lazy init via `SemaphoreSlim` + `_initialized` flag calling `EnsureCreatedAsync`
- `SaveAsync`: INSERT `backtest_runs` + N `backtest_data_subscriptions` in a transaction
- `SaveOptimizationAsync`: INSERT `optimization_runs` + N `backtest_runs` (each with subscriptions) in a transaction
- `GetByIdAsync`: SELECT + JOIN to `backtest_data_subscriptions`, deserialize JSON columns
- `QueryAsync`: Dynamic WHERE clause, JOIN to subscriptions when filtering by asset/exchange/TF, ORDER BY `completed_at DESC`, LIMIT/OFFSET
- JSON serialization via `System.Text.Json.JsonSerializer`

**Modify** `src/AlgoTradeForge.Infrastructure/DependencyInjection.cs` — register `SqliteBacktestRunRepository` as singleton

### Phase 5: WebApi — Endpoints & Config

**Modify** `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`:
- Wire existing `GET /{id:guid}` stub to `GetBacktestByIdQuery` handler
- Add `GET /` with query params: `strategyName`, `assetName`, `exchange`, `timeFrame`, `from`, `to`, `limit`, `offset`

**Modify** `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`:
- Add `GET /{id:guid}` for single optimization with all trials
- Add `GET /` with query params for listing

**Modify** `src/AlgoTradeForge.WebApi/Program.cs` — add `builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("Persistence"))`

**Modify** `src/AlgoTradeForge.WebApi/appsettings.json` — add `"Persistence": {}`

### Phase 6: Tests

**Create** `tests/AlgoTradeForge.Infrastructure.Tests/Persistence/SqliteBacktestRunRepositoryTests.cs` (uses temp file SQLite DB, `IDisposable` cleanup):
- Save + GetById round-trips all fields (fills, equity curve, params, metrics, subscriptions)
- GetById returns null for non-existent
- Query filters: by strategy name, by asset+exchange, by timeframe, by date range
- Query pagination (limit/offset)
- Query chronological order (completed_at DESC)
- SaveOptimization persists parent + children
- GetOptimizationById returns with all trials

---

## File Summary

### New files (12)
| File | Purpose |
|------|---------|
| `Application/Persistence/PersistenceOptions.cs` | Config record |
| `Application/Persistence/BacktestRunRecord.cs` | Persistence models (+ FillRecord, DataSubscriptionRecord) |
| `Application/Persistence/OptimizationRunRecord.cs` | Optimization group model |
| `Application/Persistence/BacktestRunQuery.cs` | Query filter records |
| `Application/Persistence/IBacktestRunRepository.cs` | Repository interface |
| `Application/Backtests/GetBacktestByIdQuery.cs` | Query + handler |
| `Application/Backtests/ListBacktestRunsQuery.cs` | Query + handler |
| `Application/Optimization/GetOptimizationByIdQuery.cs` | Query + handler |
| `Application/Optimization/ListOptimizationRunsQuery.cs` | Query + handler |
| `Infrastructure/Persistence/SqliteDbInitializer.cs` | Schema creation |
| `Infrastructure/Persistence/SqliteBacktestRunRepository.cs` | SQLite implementation |
| `Infrastructure.Tests/Persistence/SqliteBacktestRunRepositoryTests.cs` | Integration tests |

### Modified files (11)
| File | Change |
|------|--------|
| `Domain/Strategy/IStrategy.cs` | Add `Version` property |
| `Domain/Strategy/StrategyBase.cs` | Add `abstract Version` |
| `Domain/Strategy/ZigZagBreakout/ZigZagBreakoutStrategy.cs` | Add `Version => "1.0.0"` |
| `Application/Backtests/RunBacktestCommandHandler.cs` | Persist after compute |
| `Application/Optimization/RunOptimizationCommandHandler.cs` | Persist optimization + trials |
| `Application/DependencyInjection.cs` | Register query handlers |
| `Infrastructure/AlgoTradeForge.Infrastructure.csproj` | Add Microsoft.Data.Sqlite |
| `Infrastructure/DependencyInjection.cs` | Register repository |
| `WebApi/Endpoints/BacktestEndpoints.cs` | Wire GET + add list endpoint |
| `WebApi/Endpoints/OptimizationEndpoints.cs` | Add GET + list endpoints |
| `WebApi/Program.cs` + `appsettings.json` | Config binding |

---

## Verification

1. `dotnet build AlgoTradeForge.slnx` — all projects compile
2. `dotnet test` — all existing + new tests pass
3. Run WebApi, POST a backtest via Swagger, then GET it back by ID
4. POST an optimization, then GET it back with all trials
5. Use `GET /api/backtests?strategyName=ZigZagBreakout&assetName=BTCUSDT` to verify filtering
