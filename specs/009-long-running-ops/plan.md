# Implementation Plan: Long-Running Operations Flow

**Branch**: `009-long-running-ops` | **Date**: 2026-02-22 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/009-long-running-ops/spec.md`

## Summary

Redesign the backtest and optimization command handlers from synchronous HTTP-blocking execution to a fire-and-forget background model. POST endpoints return `202 Accepted` with a run ID and total count immediately. New status endpoints allow polling for progress (bars/combinations processed) and return the full results via a nullable result field upon completion. An in-memory `ConcurrentDictionary`-based progress store tracks volatile run state. The frontend polls automatically using TanStack Query's `refetchInterval` and displays "Processed X / Total" progress. Cancellation is supported via dedicated cancel endpoints that trigger `CancellationTokenSource`.

## Technical Context

**Language/Version**: C# 14 / .NET 10
**Primary Dependencies**: ASP.NET Core (minimal APIs), System.Threading (Task.Run, ConcurrentDictionary, Interlocked, CancellationTokenSource), existing Domain/Application/Infrastructure layers
**Storage**: SQLite (existing, via SqliteRunRepository) for completed results; ConcurrentDictionary (in-memory, volatile) for in-progress state
**Testing**: xUnit + NSubstitute
**Target Platform**: Windows (local single-user development tool)
**Project Type**: Web application (C# backend + Next.js frontend)
**Performance Goals**: POST submission response < 2 seconds; status poll response < 100ms
**Constraints**: Single-user, single-node; no distributed coordination; in-memory progress does not survive server restart
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
| Background Jobs | Job framework with persistence | JUSTIFIED DEVIATION | See Complexity Tracking |
| Async/Concurrency | async/await, CancellationToken propagation | PASS | Already wired end-to-end |
| DI Lifetimes | Singleton for progress store (no captive deps) | PASS | Store is thread-safe, no scoped injections |
| Int64 Money Convention | long for Domain money types | PASS | Existing convention maintained |

**Post-Design Re-check**: All gates remain PASS. The justified deviation from the Background Jobs requirement is documented below.

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
│   │   ├── BacktestProgress.cs         # In-memory backtest progress state
│   │   ├── OptimizationProgress.cs     # In-memory optimization progress state
│   │   ├── IRunProgressStore.cs        # Interface for volatile progress store
│   │   └── InMemoryRunProgressStore.cs # ConcurrentDictionary-based implementation
│   ├── Backtests/
│   │   ├── RunBacktestCommandHandler.cs        # MODIFIED — fire-and-forget pattern
│   │   └── ProgressTrackingEventBusSink.cs     # NEW — IEventBus sink counting BarEvents
│   ├── Optimization/
│   │   └── RunOptimizationCommandHandler.cs    # MODIFIED — fire-and-forget + per-trial error handling
│   ├── Persistence/
│   │   └── BacktestRunRecord.cs                # MODIFIED — add ErrorMessage, ErrorStackTrace
│   └── DependencyInjection.cs                  # MODIFIED — register IRunProgressStore
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
│   │   └── InMemoryRunProgressStoreTests.cs     # NEW
│   └── Backtests/
│       └── ProgressTrackingEventBusSinkTests.cs # NEW
```

**Structure Decision**: Follows existing clean architecture layout. New `Progress/` folder in Application layer for all progress-tracking types (interface + implementation collocated, matching `InMemoryDebugSessionStore` pattern). No new projects needed.

## Complexity Tracking

> **Justified deviations from Constitution Background Jobs requirements**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| No job framework (Hangfire/Quartz) | Single-user local tool; `Task.Run()` + `ConcurrentDictionary` is sufficient | Hangfire adds persistence layer, dashboard, serialization constraints — violates Principle VI (YAGNI) for a local dev tool |
| No checkpoint/resumability | Spec explicitly states in-memory state is volatile and doesn't survive restarts | Checkpoint persistence adds DB tables, serialization, recovery logic for a scenario (server restart mid-run) that is rare and acceptable to lose |
| No dead-letter queue | Failed runs store error info in BacktestRunRecord fields | Dead-letter queue adds messaging infrastructure for a single-user local tool |
| No distributed locking | Single-node, single-user system with no concurrent access concerns | N/A — the constraint doesn't apply |
