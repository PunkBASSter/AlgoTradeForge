# Quickstart: Optimization Task Queue

## Overview

This feature replaces the fire-and-forget `Task.Factory.StartNew()` pattern in optimization/validation handlers with a singleton `Channel<T>`-based task queue consumed by a `BackgroundService`. Tasks execute one at a time, with per-DSS trial cache reuse between optimization and validation.

## Key Concepts

- **ComputeTaskQueue**: Singleton `Channel<ComputeTask>` wrapper (Application layer). Holds all pending/in-progress task state.
- **ComputeQueueConsumer**: `BackgroundService` (WebApi layer). Dequeues and executes tasks sequentially.
- **OptimizationTaskExecutor / ValidationTaskExecutor**: Extracted execution logic from existing command handlers (Application layer). Called by the consumer.
- **Trial cache handoff**: Consumer holds `BoundedTrialQueue` results after optimization and injects them into the validation executor for the same DSS.

## Architecture

```
API Request → Handler.HandleAsync()
                ├── Validate, resolve axes, insert DB placeholders
                └── ComputeTaskQueue.Enqueue(tasks)
                    └── Returns 202 immediately

ComputeQueueConsumer.ExecuteAsync()
    └── await foreach (task in channel.Reader.ReadAllAsync(ct))
        ├── OptimizationTaskExecutor.ExecuteAsync() [for opt tasks]
        │   └── Parallel trial evaluation (existing logic)
        ├── Persist results (Task.Run, concurrent with next task)
        └── ValidationTaskExecutor.ExecuteAsync() [for val tasks]
            └── Uses in-memory trial cache (skips DB load)
```

## File Map

### New Files

| File | Layer | Purpose |
|------|-------|---------|
| `Application/Optimization/ComputeTask.cs` | Application | Task types, enums, ComputeTask record |
| `Application/Optimization/ComputeTaskQueue.cs` | Application | Channel wrapper + task state management |
| `Application/Optimization/OptimizationTaskExecutor.cs` | Application | Extracted brute-force/genetic execution |
| `Application/Validation/ValidationTaskExecutor.cs` | Application | Extracted validation pipeline execution |
| `WebApi/ComputeQueueConsumer.cs` | WebApi | BackgroundService consumer |
| `WebApi/Endpoints/TaskQueueEndpoints.cs` | WebApi | GET /api/queue, POST cancel, POST purge |
| `WebApi/Contracts/TaskQueueContracts.cs` | WebApi | Request/response DTOs |
| `frontend/types/task-queue.ts` | Frontend | TypeScript type definitions |
| `frontend/hooks/use-task-queue.ts` | Frontend | TanStack Query polling hook |
| `frontend/components/features/dashboard/task-queue-panel.tsx` | Frontend | Queue panel UI component |

### Modified Files

| File | Change |
|------|--------|
| `Application/Optimization/RunGroupOptimizationCommandHandler.cs` | Remove `Task.Factory.StartNew()`, enqueue to channel instead |
| `Application/Optimization/RunOptimizationCommandHandler.cs` | Same — enqueue single-DSS as group-of-1 |
| `Application/Optimization/RunGeneticOptimizationCommandHandler.cs` | Same — enqueue genetic task |
| `Application/Validation/RunGroupValidationCommandHandler.cs` | Remove `Task.Factory.StartNew()`, enqueue to channel |
| `Application/Validation/RunValidationCommandHandler.cs` | Same — enqueue single validation |
| `Application/DependencyInjection.cs` | Register ComputeTaskQueue, executors |
| `WebApi/Program.cs` | Register ComputeQueueConsumer as hosted service |
| `WebApi/Contracts/RunOptimizationRequest.cs` | Add `Validate`, `ThresholdProfileName`, `MaxThreads` fields |
| `WebApi/Endpoints/OptimizationEndpoints.cs` | Pass new fields to command |
| `WebApi/Endpoints/ValidationEndpoints.cs` | Route through queue |
| `frontend/lib/services/api-client.ts` | Add queue API functions |
| `frontend/components/features/dashboard/run-new-panel.tsx` | Add validate toggle, max threads, profile |

## Build & Test

```bash
# Build
dotnet build AlgoTradeForge.slnx

# Test (sequential, never parallel)
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.Application.Tests/

# Run WebApi
dotnet run --project src/AlgoTradeForge.WebApi/

# Frontend dev
cd frontend && npm run dev
```

## Verification

1. Submit a multi-DSS optimization with `validate: true`
2. Verify `GET /api/queue` shows tasks in order: opt#0, val#0, opt#1, val#1
3. Verify only one task is `InProgress` at any time
4. Verify validation skips DB trial loading (check logs for "Using cached trials")
5. Cancel a pending task, verify cascade cancellation of related validation
6. Purge pending, verify all pending tasks cleared
