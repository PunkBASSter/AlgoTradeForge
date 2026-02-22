# Implementation Plan: Long-Running Operations Flow

**Branch**: `009-long-running-ops` | **Date**: 2026-02-22 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/009-long-running-ops/spec.md`

## Summary

Redesign the backtest and optimization command handlers from synchronous HTTP-blocking execution to a fire-and-forget background model. POST endpoints return `202 Accepted` with a RunKey and total count immediately. The RunKey is deterministic (`Strategy_Version_Period_ParamsHash`) — submitting identical parameters deduplicates to the existing run. New status endpoints allow polling for progress (bars/combinations processed) via `IDistributedCache` (key = RunKey, value = progress int) and return the full results via a nullable result field upon completion. Locally backed by `AddDistributedMemoryCache()`; swappable to Redis via DI. The frontend polls automatically using TanStack Query's `refetchInterval` and displays "Processed X / Total" progress. Cancellation is supported via dedicated cancel endpoints that trigger `CancellationTokenSource`.

## Technical Context

**Language/Version**: C# 14 / .NET 10
**Primary Dependencies**: ASP.NET Core (minimal APIs), System.Threading (Task.Run, Interlocked, CancellationTokenSource), Microsoft.Extensions.Caching.Distributed (IDistributedCache), existing Domain/Application/Infrastructure layers
**Storage**: SQLite (existing, via SqliteRunRepository) for completed results; `IDistributedCache` (`AddDistributedMemoryCache()`) for in-progress state (swappable to Redis via DI)
**Testing**: xUnit + NSubstitute
**Target Platform**: Windows (local single-user development tool)
**Project Type**: Web application (C# backend + Next.js frontend)
**Performance Goals**: POST submission response < 2 seconds; status poll response < 100ms
**Constraints**: Single-user, single-node; no distributed coordination; in-memory progress (via AddDistributedMemoryCache) does not survive server restart; swappable to Redis via DI
**Scale/Scope**: One concurrent user, up to ~100K optimization combinations

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Status | Notes |
|-----------|------|--------|-------|
| I. Strategy-as-Code | No changes to strategy interface | PASS | Not affected |
| II. Test-First | Tests written before implementation | PASS | Required |
| III. Data Integrity | Historical data immutability preserved | PASS | Not affected |
| IV. Observability | Structured telemetry for background ops | PASS | Background task errors logged via Serilog |
| V. Separation of Concerns | Backend MUST NOT block on long-running ops | PASS | **This feature fixes this violation** |
| VI. Simplicity & YAGNI | Simplest solution that meets requirements | PASS | Simple Task.Run + ConcurrentDictionary |
| Background Jobs | Ephemeral compute run rules (v1.6.0) | PASS | RunKey dedup via IDistributedCache; no DLQ/checkpoint needed per constitution |
| Async/Concurrency | async/await, CancellationToken propagation | PASS | Already wired end-to-end |
| DI Lifetimes | IDistributedCache registered via AddDistributedMemoryCache() | PASS | Standard Singleton registration, no captive deps |
| Int64 Money Convention | long for Domain money types | PASS | Existing convention maintained |

**Post-Design Re-check**: All gates PASS. Constitution v1.6.0 classifies backtests/optimizations as Ephemeral Compute Runs with appropriate rules.

## Project Structure

### Documentation (this feature)

```text
specs/009-long-running-ops/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   └── api-contracts.md
└── tasks.md             # Phase 2 output (via /speckit.tasks)
```

### Source Code (repository root)

```text
src/
├── AlgoTradeForge.Domain/              # No changes needed
├── AlgoTradeForge.Application/
│   ├── Progress/                       # NEW — Progress tracking types
│   │   ├── RunStatus.cs                # Enum: Pending, Running, Completed, Failed, Cancelled
│   │   └── RunKeyBuilder.cs            # Deterministic RunKey generation (Strategy+Version+Period+ParamsHash)
│   ├── Backtests/
│   │   ├── RunBacktestCommandHandler.cs        # MODIFIED — fire-and-forget pattern
│   │   └── ProgressTrackingEventBusSink.cs     # NEW — IEventBus sink counting BarEvents
│   ├── Optimization/
│   │   └── RunOptimizationCommandHandler.cs    # MODIFIED — fire-and-forget + per-trial error handling
│   ├── Persistence/
│   │   └── BacktestRunRecord.cs                # MODIFIED — add ErrorMessage, ErrorStackTrace
│   └── DependencyInjection.cs                  # MODIFIED — register AddDistributedMemoryCache()
├── AlgoTradeForge.Infrastructure/
│   └── Persistence/
│       └── SqliteRunRepository.cs              # MODIFIED — handle error columns
└── AlgoTradeForge.WebApi/
    ├── Contracts/
    │   ├── SubmissionResponses.cs               # NEW — BacktestSubmission, OptimizationSubmission
    │   ├── StatusResponses.cs                   # NEW — BacktestStatus, OptimizationStatus
    │   └── RunContracts.cs                      # MODIFIED — add error fields to BacktestRunResponse
    └── Endpoints/
        ├── BacktestEndpoints.cs                 # MODIFIED — POST 202, GET status, POST cancel
        └── OptimizationEndpoints.cs             # MODIFIED — POST 202, GET status, POST cancel

frontend/
├── types/
│   └── api.ts                                   # MODIFIED — add status/submission types
├── lib/services/
│   └── api-client.ts                            # MODIFIED — add status/cancel methods
├── hooks/
│   └── use-run-status.ts                        # NEW — polling hooks
├── components/features/dashboard/
│   ├── run-new-panel.tsx                         # MODIFIED — async submission + progress
│   └── run-progress.tsx                          # NEW — progress display component
└── app/report/
    ├── backtest/[id]/page.tsx                    # MODIFIED — show error details
    └── optimization/[id]/page.tsx                # MODIFIED — show error details for trials

tests/
├── AlgoTradeForge.Application.Tests/            # NEW test project (if not exists) or existing
│   ├── Progress/
│   │   └── RunKeyBuilderTests.cs                # NEW
│   └── Backtests/
│       └── ProgressTrackingEventBusSinkTests.cs # NEW
```

**Structure Decision**: Follows existing clean architecture layout. New `Progress/` folder in Application layer for RunKey generation and status enum. Progress tracking uses `IDistributedCache` (standard .NET abstraction) — no custom store interface needed. `AddDistributedMemoryCache()` registered in DI; swappable to Redis with zero code changes.

## Complexity Tracking

> **Constitution Compliance (v1.6.0 — Ephemeral Compute Runs)**

| Requirement | Implementation | Status |
|-------------|---------------|--------|
| Deduplicate by RunKey | Deterministic key = `{Strategy}_{Version}_{Period}_{ParamsHash}`; checked in `IDistributedCache` before starting a new run | COMPLIANT |
| Progress via `IDistributedCache` | `AddDistributedMemoryCache()` locally; key = RunKey, value = progress `int`; swappable to Redis via DI registration | COMPLIANT |
| No DLQ required | Failed runs store error in `BacktestRunRecord` columns; client can resubmit | N/A per constitution |
| No checkpoint/resumability required | Atomic compute — partial results carry no value | N/A per constitution |
| No distributed locking required | Single-node deployment | N/A per constitution |
