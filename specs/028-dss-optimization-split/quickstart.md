# Quickstart: Per-DSS Optimization Split

**Branch**: `028-dss-optimization-split` | **Date**: 2026-04-13

## What This Feature Changes

Optimization currently treats data subscriptions (DSS) as just another parameter axis — all DSS combinations are mixed into one giant Cartesian product, blocking results until everything finishes. This feature splits each DSS into its own independent optimization run, grouped under a single "optimization group."

## Key Concepts

- **Optimization Group**: N independent optimization runs launched together, one per DSS
- **Shared Parallelism**: All DSS runs share a single `MaxDegreeOfParallelism` pool via `Channel<T>` work queue
- **Immediate Results**: Each DSS run saves results independently — no waiting for others
- **Cross-DSS Table**: Combined view of all trials across all DSS in a group
- **Validation Group**: Per-DSS validation runs mirroring the optimization group structure

## Build & Test

```bash
# Build
dotnet build AlgoTradeForge.slnx

# Run domain tests
dotnet test tests/AlgoTradeForge.Domain.Tests/

# Run application tests
dotnet test tests/AlgoTradeForge.Application.Tests/

# Run infrastructure tests
dotnet test tests/AlgoTradeForge.Infrastructure.Tests/

# Run WebApi integration tests
dotnet test tests/AlgoTradeForge.WebApi.Tests/

# Run frontend tests
cd frontend && npm test

# Full private strategies build
dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx
```

## DB Reset

Since this feature drops existing optimization data (FR-014), delete the SQLite DB file to force a clean schema recreation:

```bash
# The DB file location depends on configuration; typically:
rm -f data/algotrade.db
```

The `SqliteDbInitializer` recreates all tables on startup.

## API Quick Test

```bash
# Launch a 3-DSS optimization group
curl -X POST http://localhost:5000/api/optimizations \
  -H "Content-Type: application/json" \
  -d '{
    "strategyName": "BuyAndHold",
    "backtestSettings": { "initialCash": 10000, "startTime": "2024-01-01", "endTime": "2025-01-01" },
    "optimizationAxes": {},
    "subscriptionAxis": [
      [{"assetName": "BTC", "exchange": "binance", "timeFrame": "1d"}],
      [{"assetName": "ETH", "exchange": "binance", "timeFrame": "1d"}]
    ]
  }'

# Check group status
curl http://localhost:5000/api/optimizations/groups/{groupId}/status

# Get cross-DSS trials
curl http://localhost:5000/api/optimizations/groups/{groupId}/trials?sortBy=SharpeRatio
```

## Files Changed (High-Level)

### Backend (C#)
- `Application/Optimization/` — New single group command + Channel<T> handler
- `Application/Persistence/` — New `OptimizationGroupRecord`, `ValidationGroupRecord`
- `Application/Validation/` — New group validation command + handler
- `Application/Progress/` — `RunKeyBuilder` extended with `BuildGroupKey()`
- `Infrastructure/Persistence/` — Schema changes, denormalized metrics, new repository methods
- `WebApi/Endpoints/` — Group sub-routes added to existing endpoint classes
- `WebApi/Contracts/` — New request/response DTOs

### Frontend (TypeScript)
- `frontend/types/` — Group types added to existing type files
- `frontend/hooks/` — Group hooks added to existing hook files
- `frontend/components/features/dashboard/` — Visual DSS builder, group list
- `frontend/components/features/report/` — Cross-DSS table, Params column, clickable trial IDs
- `frontend/app/report/` — New group report pages

### Database
- New tables: `optimization_groups` (with stored `status`), `validation_groups` (with stored `status`)
- Modified: `optimization_runs` (+group_id, +dss_index), `backtest_runs` (+denormalized metric columns), `validation_runs` (+validation_group_id)
- Existing optimization/validation data dropped
