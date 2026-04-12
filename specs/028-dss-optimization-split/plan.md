# Implementation Plan: Per-DSS Optimization Split

**Branch**: `028-dss-optimization-split` | **Date**: 2026-04-13 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/028-dss-optimization-split/spec.md`

## Summary

Split optimization execution so each data subscription set (DSS) runs as an independent optimization within a new "Optimization Group" entity. Results per DSS are available immediately upon completion. The frontend shows groups as expandable rows with cross-DSS comparison tables. Validation mirrors the group structure. A shared `Channel<T>` work queue with `maxParallelism` consumers provides true deterministic round-robin scheduling across DSS runs.

## Technical Context

**Language/Version**: C# 14 / .NET 10 (backend), TypeScript 5.x strict (frontend)
**Primary Dependencies**: ASP.NET Core minimal APIs, System.Threading, TanStack Query, Next.js 16, CodeMirror 6
**Storage**: SQLite via SqliteRunRepository (existing) + new tables for groups
**Testing**: xUnit + NSubstitute (backend), Vitest + Testing Library (frontend)
**Target Platform**: Windows (dev), Linux (deploy)
**Project Type**: Web application (backend API + Next.js frontend)
**Performance Goals**: Sorting 10,000 trials < 1 second; first DSS results visible within seconds of that run completing
**Constraints**: Drop existing optimization/validation data (FR-014); shared parallelism pool (no per-DSS budget)
**Scale/Scope**: 3-10 DSS per group typical; up to 10,000 trials per DSS run; single-user deployment

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Strategy-as-Code | Pass | No strategy interface changes. Strategies are unaware of DSS grouping. |
| II. Test-First | Pass | Tests will be written for new group commands, handlers, and repositories. |
| III. Data Integrity | Pass | Each DSS run maintains its own data provenance. Group is a logical grouping only. |
| IV. Observability | Pass | Per-run progress tracking preserved; group status stored and updated per child completion. Structured logging for group lifecycle events (T021). |
| V. Separation of Concerns | Pass | Frontend: display + workflow. Backend: orchestration + persistence. No trading logic in frontend. Long-running ops run in background tasks. |
| VI. Simplicity & YAGNI | Pass | Group status stored and updated per child completion. Shared Channel<T> work queue reuses existing patterns. No speculative features. |

**Gate**: All principles pass. No violations to justify.

### Post-Phase 1 Re-Check

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Strategy-as-Code | Pass | Unchanged — strategies remain isolated. |
| II. Test-First | Pass | Test plan covers: group creation, per-DSS execution, shared semaphore, status derivation, cross-DSS queries, validation group launch. |
| III. Data Integrity | Pass | Schema uses FK constraints (group_id, validation_group_id). Cascade delete preserves referential integrity. |
| IV. Observability | Pass | Group lifecycle logged (created, child completed, all completed). Per-run progress preserved via RunProgressCache. |
| V. Separation of Concerns | Pass | New entities follow existing clean architecture layering (Domain records → Application commands → Infrastructure persistence → WebApi endpoints). |
| VI. Simplicity & YAGNI | Pass | No new abstractions beyond what's needed. `Channel<T>` is standard .NET. Single group command handles both brute-force and genetic modes. DB drop avoids migration complexity. |

**Gate**: All principles pass post-design.

## Project Structure

### Documentation (this feature)

```text
specs/028-dss-optimization-split/
├── spec.md              # Feature specification
├── plan.md              # This file
├── research.md          # Phase 0: research findings
├── data-model.md        # Phase 1: entity definitions and schema
├── quickstart.md        # Phase 1: build/test/run guide
├── contracts/
│   └── api-endpoints.md # Phase 1: REST API contract changes
├── checklists/
│   └── requirements.md  # Spec quality checklist
└── tasks.md             # Phase 2: task breakdown (via /speckit.tasks)
```

### Source Code (repository root)

```text
src/
  AlgoTradeForge.Domain/
    Optimization/                     # No changes needed (axes, genetics are per-run)

  AlgoTradeForge.Application/
    Optimization/
      RunGroupOptimizationCommand.cs          # NEW: single group command (Mode: BruteForce|Genetic)
      RunGroupOptimizationCommandHandler.cs   # NEW: Channel<T> fan-out orchestrator
      OptimizationSetupHelper.cs              # MODIFIED: remove AppendSubscriptionAxis path
      GroupStatusCalculator.cs                # NEW: compute + persist group status on child completion
    Persistence/
      OptimizationGroupRecord.cs              # NEW: group record
      ValidationGroupRecord.cs                # NEW: validation group record
    Validation/
      RunGroupValidationCommand.cs            # NEW: group validation command
      RunGroupValidationCommandHandler.cs     # NEW: fan-out validation
    Progress/
      RunKeyBuilder.cs                        # MODIFIED: add BuildGroupKey() method
    Repositories/
      IRunRepository.cs                       # MODIFIED: add group CRUD methods

  AlgoTradeForge.Infrastructure/
    Persistence/
      SqliteDbInitializer.cs                  # MODIFIED: new tables, denormalized metrics, drop data
      SqliteRunRepository.cs                  # MODIFIED: group queries, cross-DSS trials, metrics columns
      SqliteValidationRepository.cs           # MODIFIED: validation group queries

  AlgoTradeForge.WebApi/
    Contracts/
      OptimizationGroupContracts.cs           # NEW: group request/response DTOs
      ValidationGroupContracts.cs             # NEW: validation group DTOs
    Endpoints/
      OptimizationEndpoints.cs                # MODIFIED: list returns groups, group sub-routes added
      ValidationEndpoints.cs                  # MODIFIED: list returns groups, group sub-routes added

frontend/
  types/
    optimization.ts                           # MODIFIED: add group types alongside existing
    validation.ts                             # MODIFIED: add validation group types
    api.ts                                    # MODIFIED: updated submission response
  hooks/
    use-optimizations.ts                      # MODIFIED: add group hooks, trials include params field
    use-validations.ts                        # MODIFIED: add validation group hooks
  components/features/
    dashboard/
      run-new-panel.tsx                       # MODIFIED: integrate DSS builder
      dss-builder.tsx                         # NEW: collapsible visual DSS row builder
    report/
      optimization-group-page.tsx             # NEW: group report with tabs
      cross-dss-trials-table.tsx              # NEW: cross-DSS comparison table
      optimization-trials-table.tsx           # MODIFIED: Params column, sortable, clickable IDs
      trial-backtest-panel.tsx                # NEW: side panel for backtest from trial
      validation-group-page.tsx               # NEW: validation group report
    optimization/
      optimization-groups-table.tsx           # NEW: expandable group rows
  app/
    report/
      optimization-group/[groupId]/page.tsx   # NEW: group report route
      validation-group/[groupId]/page.tsx     # NEW: validation group route
    [strategy]/
      optimization/page.tsx                   # MODIFIED: use groups table

tests/
  AlgoTradeForge.Application.Tests/
    Optimization/
      RunGroupOptimizationCommandHandlerTests.cs  # NEW
      GroupStatusCalculatorTests.cs                # NEW
    Validation/
      RunGroupValidationCommandHandlerTests.cs    # NEW
  AlgoTradeForge.Infrastructure.Tests/
    Persistence/
      SqliteRunRepository_GroupTests.cs            # NEW
      SqliteValidationRepository_GroupTests.cs     # NEW
  AlgoTradeForge.WebApi.Tests/
    OptimizationEndpointGroupTests.cs              # NEW
    ValidationEndpointGroupTests.cs                # NEW
```

**Structure Decision**: Follows existing clean architecture. Single `RunGroupOptimizationCommand` with `Mode` field handles both brute-force and genetic optimization — per-DSS execution uses `helper.ExecuteTrial()` directly via Channel consumers, not separate command handlers. Group endpoints are nested under existing `/api/optimizations/groups/` and `/api/validations/groups/` paths rather than separate top-level routes. Frontend group hooks are added to existing hook files rather than creating new ones.

## Complexity Tracking

No constitution violations. No complexity justifications needed.
