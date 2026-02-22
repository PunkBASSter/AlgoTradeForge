# Quickstart: Long-Running Operations Flow

**Feature**: 009-long-running-ops

## Prerequisites

- .NET 10 SDK
- Node.js 20+
- Existing AlgoTradeForge solution builds successfully

## Backend Changes Overview

### New Files

| File | Layer | Purpose |
|------|-------|---------|
| `src/AlgoTradeForge.Application/Progress/RunStatus.cs` | Application | Enum: Pending, Running, Completed, Failed, Cancelled |
| `src/AlgoTradeForge.Application/Progress/BacktestProgress.cs` | Application | In-memory progress state for backtests |
| `src/AlgoTradeForge.Application/Progress/OptimizationProgress.cs` | Application | In-memory progress state for optimizations |
| `src/AlgoTradeForge.Application/Progress/IRunProgressStore.cs` | Application | Interface for volatile progress store |
| `src/AlgoTradeForge.Application/Progress/InMemoryRunProgressStore.cs` | Application | ConcurrentDictionary-based implementation |
| `src/AlgoTradeForge.Application/Backtests/ProgressTrackingEventBusSink.cs` | Application | IEventBus sink that counts BarEvents for progress |
| `src/AlgoTradeForge.WebApi/Contracts/SubmissionResponses.cs` | WebApi | BacktestSubmissionResponse, OptimizationSubmissionResponse |
| `src/AlgoTradeForge.WebApi/Contracts/StatusResponses.cs` | WebApi | BacktestStatusResponse, OptimizationStatusResponse |

### Modified Files

| File | Change |
|------|--------|
| `src/AlgoTradeForge.Application/Persistence/BacktestRunRecord.cs` | Add ErrorMessage, ErrorStackTrace fields |
| `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs` | Refactor to fire-and-forget with progress store |
| `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs` | Refactor to fire-and-forget with progress store, per-trial error handling |
| `src/AlgoTradeForge.Application/DependencyInjection.cs` | Register IRunProgressStore as Singleton |
| `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs` | POST returns 202, add GET status + POST cancel |
| `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` | POST returns 202, add GET status + POST cancel |
| `src/AlgoTradeForge.WebApi/Contracts/RunContracts.cs` | Add error fields to BacktestRunResponse |
| `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs` | Handle error fields in INSERT/SELECT |

### Test Files

| File | Purpose |
|------|---------|
| `tests/AlgoTradeForge.Application.Tests/Progress/InMemoryRunProgressStoreTests.cs` | Unit tests for progress store |
| `tests/AlgoTradeForge.Application.Tests/Backtests/RunBacktestCommandHandlerTests.cs` | Updated handler tests |
| `tests/AlgoTradeForge.Application.Tests/Optimization/RunOptimizationCommandHandlerTests.cs` | Updated handler tests |

## Frontend Changes Overview

### New Files

| File | Purpose |
|------|---------|
| `frontend/hooks/use-run-status.ts` | Polling hooks for backtest/optimization status |
| `frontend/components/features/dashboard/run-progress.tsx` | Progress display component |

### Modified Files

| File | Change |
|------|--------|
| `frontend/lib/services/api-client.ts` | Add status and cancel methods, new return types |
| `frontend/types/api.ts` | Add status/submission types, error fields on BacktestRun |
| `frontend/components/features/dashboard/run-new-panel.tsx` | Switch to async submission with progress tracking |
| `frontend/app/report/backtest/[id]/page.tsx` | Display error details for failed runs |
| `frontend/app/report/optimization/[id]/page.tsx` | Display error details for failed trials |

## How to Verify

1. **Backend only** (API):
   ```bash
   # Start the API
   dotnet run --project src/AlgoTradeForge.WebApi

   # Submit a backtest (returns 202 with run ID)
   curl -X POST http://localhost:5000/api/backtests/ -H "Content-Type: application/json" -d '{"assetName":"BTCUSDT","exchange":"Binance","strategyName":"SmaCrossover","initialCash":10000,"startTime":"2024-01-01","endTime":"2024-12-31"}'

   # Poll for status (use the returned ID)
   curl http://localhost:5000/api/backtests/{id}/status

   # Cancel if needed
   curl -X POST http://localhost:5000/api/backtests/{id}/cancel
   ```

2. **Full stack** (with frontend):
   ```bash
   # Terminal 1: Backend
   dotnet run --project src/AlgoTradeForge.WebApi

   # Terminal 2: Frontend
   cd frontend && npm run dev

   # Open http://localhost:3000/dashboard
   # Submit a run and observe progress bar updating
   ```

3. **Run tests**:
   ```bash
   dotnet test
   ```
