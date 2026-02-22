# Tasks: Long-Running Operations Flow

**Input**: Design documents from `/specs/009-long-running-ops/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Included per Constitution Principle II (Test-First, non-negotiable).

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: Verify build, create folder structure, and register `IDistributedCache`

- [X] T001 Verify solution builds successfully and all existing tests pass by running `dotnet build AlgoTradeForge.slnx` and `dotnet test AlgoTradeForge.slnx`
- [X] T002 Create `Progress/` folder under `src/AlgoTradeForge.Application/Progress/`
- [X] T003 Verify `tests/AlgoTradeForge.Application.Tests/` project exists; if not, create it with `dotnet new xunit`, add project references to `AlgoTradeForge.Application`, add NSubstitute package, and add it to the solution `AlgoTradeForge.slnx`
- [X] T003b Verify `tests/AlgoTradeForge.WebApi.Tests/` project exists; if not, create it with `dotnet new xunit`, add project references to `AlgoTradeForge.WebApi` and `AlgoTradeForge.Application`, add NSubstitute and `Microsoft.AspNetCore.Mvc.Testing` packages, and add it to the solution `AlgoTradeForge.slnx`
- [X] T004 Register `AddDistributedMemoryCache()` in `src/AlgoTradeForge.WebApi/Program.cs` (required before any `RunProgressCache` usage)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core types and infrastructure that ALL user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete

### Tests for Foundational

- [X] T005 [P] Write unit tests for `RunKeyBuilder`: verify deterministic output for identical params, different output for different params, parameter order independence, SHA256 format. Test both `Build(RunBacktestCommand)` and `Build(RunOptimizationCommand)` overloads in `tests/AlgoTradeForge.Application.Tests/Progress/RunKeyBuilderTests.cs`
- [X] T006 [P] Write unit tests for `RunProgressCache`: verify SetAsync/GetAsync round-trip, GetAsync returns null for missing key, RemoveAsync removes entry, TryGetRunIdByKeyAsync/SetRunKeyAsync/RemoveRunKeyAsync for dedup operations. Use `AddDistributedMemoryCache()` as real implementation in `tests/AlgoTradeForge.Application.Tests/Progress/RunProgressCacheTests.cs`
- [X] T007 [P] Write unit tests for `InMemoryRunCancellationRegistry`: verify Register/TryCancel/TryGetToken/Remove; verify TryCancel returns true and triggers cancellation; verify TryCancel returns false for unknown id; verify Remove cleans up; verify thread-safety with concurrent Register/TryCancel in `tests/AlgoTradeForge.Application.Tests/Progress/InMemoryRunCancellationRegistryTests.cs`
- [X] T008 [P] Write unit tests for `ProgressTrackingEventBusSink` (increments ProcessedBars counter on every Write call, counter readable via property) in `tests/AlgoTradeForge.Application.Tests/Backtests/ProgressTrackingEventBusSinkTests.cs`

### Implementation for Foundational

- [X] T009 [P] Create `RunStatus` enum (Pending, Running, Completed, Failed, Cancelled) in `src/AlgoTradeForge.Application/Progress/RunStatus.cs`
- [X] T010 [P] Create `RunProgressEntry` record with fields per data-model.md (Id, Status, Processed, Failed, Total, ErrorMessage, ErrorStackTrace, StartedAt) in `src/AlgoTradeForge.Application/Progress/RunProgressEntry.cs`
- [X] T011 Create `RunKeyBuilder` static class with `Build(RunBacktestCommand)` and `Build(RunOptimizationCommand)` methods that produce a SHA256 hash of canonical parameter string in `src/AlgoTradeForge.Application/Progress/RunKeyBuilder.cs`
- [X] T012 Create `RunProgressCache` class wrapping `IDistributedCache` with typed Get/Set/Remove for `RunProgressEntry` (key: `progress:{guid}`) and dedup operations TryGetRunIdByKey/SetRunKey/RemoveRunKey (key: `runkey:{runKey}`) in `src/AlgoTradeForge.Application/Progress/RunProgressCache.cs`
- [X] T013 [P] Create `IRunCancellationRegistry` interface with Register/TryCancel/TryGetToken/Remove methods in `src/AlgoTradeForge.Application/Progress/IRunCancellationRegistry.cs`
- [X] T014 [P] Implement `InMemoryRunCancellationRegistry` using `ConcurrentDictionary<Guid, CancellationTokenSource>` in `src/AlgoTradeForge.Application/Progress/InMemoryRunCancellationRegistry.cs`
- [X] T015 Register `RunProgressCache` as Singleton and `IRunCancellationRegistry` → `InMemoryRunCancellationRegistry` as Singleton in `src/AlgoTradeForge.Application/DependencyInjection.cs`. Note: handler DI registrations (`ICommandHandler<RunBacktestCommand, *>` and `ICommandHandler<RunOptimizationCommand, *>`) will be updated in T028/T034 when the handler return types change.
- [X] T016 Add `ErrorMessage` (string?) and `ErrorStackTrace` (string?) fields to `BacktestRunRecord` in `src/AlgoTradeForge.Application/Persistence/BacktestRunRecord.cs`
- [X] T017 Update `SqliteRunRepository` to read/write `error_message` and `error_stack_trace` columns in INSERT and SELECT queries for `backtest_runs` table. Add migration v3 with the two nullable TEXT columns via ALTER TABLE in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs`
- [X] T018 Create `ProgressTrackingEventBusSink` implementing `ISink` that increments a `long` counter via `Interlocked.Increment` on each `Write` call, with a `ProcessedBars` property using `Interlocked.Read` in `src/AlgoTradeForge.Application/Backtests/ProgressTrackingEventBusSink.cs`
- [X] T019 [P] Create `BacktestSubmissionDto` record (Id, TotalBars, IsDedup) in `src/AlgoTradeForge.Application/Backtests/BacktestSubmissionDto.cs`
- [X] T020 [P] Create `OptimizationSubmissionDto` record (Id, TotalCombinations, IsDedup) in `src/AlgoTradeForge.Application/Optimization/OptimizationSubmissionDto.cs`
- [X] T021 [P] Create `BacktestSubmissionResponse` and `OptimizationSubmissionResponse` records per data-model.md (including `IsDedup` field) in `src/AlgoTradeForge.WebApi/Contracts/SubmissionResponses.cs`
- [X] T022 [P] Create `BacktestStatusResponse` and `OptimizationStatusResponse` records per data-model.md (with nullable Result field) in `src/AlgoTradeForge.WebApi/Contracts/StatusResponses.cs`
- [X] T023 [P] Add `ErrorMessage` (string?) and `ErrorStackTrace` (string?) fields to `BacktestRunResponse` in `src/AlgoTradeForge.WebApi/Contracts/RunContracts.cs` and update the mapping from `BacktestRunRecord`
- [X] T023b [P] Add `ErrorMessage` (string?) and `ErrorStackTrace` (string?) fields to `OptimizationTrialResultDto` in `src/AlgoTradeForge.Application/Optimization/OptimizationResult.cs` and update the mapping in `RunOptimizationCommandHandler` to populate them from caught exceptions
- [X] T024 [P] Add frontend TypeScript types: `BacktestSubmission`, `OptimizationSubmission`, `BacktestStatus`, `OptimizationStatus`, `RunStatusType` union, and add `errorMessage?`/`errorStackTrace?` to `BacktestRun` in `frontend/types/api.ts`
- [X] T025 [P] Add API client methods: `getBacktestStatus(id)`, `getOptimizationStatus(id)`, `cancelBacktest(id)`, `cancelOptimization(id)`, and update `runBacktest()` return type to `BacktestSubmission` and `runOptimization()` return type to `OptimizationSubmission` in `frontend/lib/services/api-client.ts`
- [X] T026 Verify all foundational tests pass by running `dotnet test AlgoTradeForge.slnx`

**Checkpoint**: All foundational types, contracts, and infrastructure ready. User story implementation can begin.

---

## Phase 3: User Story 1 — Submit Backtest and Track Progress (Priority: P1) MVP

**Goal**: POST /api/backtests/ returns 202 with run ID immediately; backtest runs in background; GET /api/backtests/{id}/status returns progress and results. Duplicate submissions with identical parameters return the existing run (dedup via RunKey).

**Independent Test**: Submit a backtest via curl, verify 202 response with run ID, poll /status until completion, confirm final results match previous synchronous output. Submit same params again while running, verify same ID returned (dedup).

### Tests for User Story 1

- [X] T027 [US1] Write tests for refactored `RunBacktestCommandHandler`: verify it (1) computes RunKey and checks cache for dedup, (2) on dedup hit returns existing ID with IsDedup=true, (3) on new run: creates RunProgressEntry in RunProgressCache, registers CTS in IRunCancellationRegistry, sets RunKey mapping, starts background task, returns submission with IsDedup=false; (4) on completion persists results to IRunRepository, updates cache entry to Completed, removes RunKey mapping, removes CTS; (5) on no candle data available, throws validation error synchronously (no background task started). Use NSubstitute for dependencies in `tests/AlgoTradeForge.Application.Tests/Backtests/RunBacktestCommandHandlerTests.cs`

### Implementation for User Story 1

- [X] T028 [US1] Refactor `RunBacktestCommandHandler` to: (1) inject `RunProgressCache` + `IRunCancellationRegistry`, (2) compute RunKey via `RunKeyBuilder.Build()`, check for dedup via `RunProgressCache.TryGetRunIdByKeyAsync()` — if active run exists return existing ID, (3) perform validation and data loading synchronously, (4) create `RunProgressEntry` with Pending status, store in cache + set RunKey mapping + register CTS, (5) start `Task.Run()` with engine.Run + `ProgressTrackingEventBusSink` + progress flush to cache every 1 second via Stopwatch + result persistence + cache/RunKey/CTS cleanup on completion/failure, (6) return `BacktestSubmissionDto` immediately. Change handler's `TResult` from `BacktestResultDto` to `BacktestSubmissionDto` in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`
- [X] T029 [US1] Modify POST /api/backtests/ endpoint to return `Results.Accepted(BacktestSubmissionResponse)` (HTTP 202) instead of `Results.Ok(BacktestResultDto)` in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`
- [X] T030 [US1] Add `GET /api/backtests/{id}/status` endpoint that: (1) checks `RunProgressCache.GetAsync()` for in-progress run, (2) if found, maps `RunProgressEntry` to `BacktestStatusResponse`, (3) if not found, checks `IRunRepository.GetByIdAsync()` for completed run and returns Completed status with full result, (4) if neither found, returns 404 in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`
- [X] T031 [US1] Write integration tests for backtest submission and status endpoints: verify POST returns 202 with BacktestSubmissionResponse; verify dedup returns same ID for identical params; verify GET /status returns progress while running and full result when completed; verify GET /status for unknown ID returns 404 in `tests/AlgoTradeForge.WebApi.Tests/Endpoints/BacktestEndpointsTests.cs`
- [X] T032 [US1] Verify US1 end-to-end: build solution, start WebApi, submit a backtest via curl, poll /status, confirm completion with full results matching expected format

**Checkpoint**: Async backtest flow with dedup works end-to-end via API. Frontend not yet updated.

---

## Phase 4: User Story 2 — Submit Optimization and Track Progress (Priority: P1)

**Goal**: POST /api/optimizations/ returns 202 with run ID immediately; optimization runs in background with per-trial error handling; GET /api/optimizations/{id}/status returns progress counts and results on completion. Duplicate submissions deduplicate via RunKey.

**Independent Test**: Submit an optimization via curl, verify 202 with run ID and totalCombinations, poll /status until completion, confirm final results match previous synchronous output including any failed trials with error details. Submit same params again while running, verify dedup.

### Tests for User Story 2

- [X] T033 [US2] Write tests for refactored `RunOptimizationCommandHandler`: verify it (1) computes RunKey and checks cache for dedup, (2) on dedup hit returns existing ID with IsDedup=true, (3) on new run: creates RunProgressEntry, registers CTS, sets RunKey mapping, starts background task, returns submission; (4) verify per-trial error handling (failed trials saved with ErrorMessage/ErrorStackTrace, successful trials unaffected); (5) on completion persists results, updates cache, removes RunKey, removes CTS; (6) on zero valid parameter combinations, throws validation error synchronously (no background task started). Use NSubstitute in `tests/AlgoTradeForge.Application.Tests/Optimization/RunOptimizationCommandHandlerTests.cs`

### Implementation for User Story 2

- [X] T034 [US2] Refactor `RunOptimizationCommandHandler` to: (1) inject `RunProgressCache` + `IRunCancellationRegistry`, (2) compute RunKey, check for dedup, (3) perform validation, axis resolution, combination estimation, data pre-loading synchronously, (4) create `RunProgressEntry` with Pending and totalCombinations, store in cache + RunKey + CTS, (5) start `Task.Run()` with `Parallel.ForEachAsync`, try/catch per trial to save failed trials with error info + `Interlocked.Increment` on Processed or Failed counters + progress flush to cache every 1 second via Stopwatch + result persistence + cleanup on completion/failure, (6) return `OptimizationSubmissionDto` immediately in `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs`
- [X] T035 [US2] Modify POST /api/optimizations/ endpoint to return `Results.Accepted(OptimizationSubmissionResponse)` (HTTP 202) in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`
- [X] T036 [US2] Add `GET /api/optimizations/{id}/status` endpoint that: (1) checks `RunProgressCache.GetAsync()` for in-progress optimization, (2) if found, maps `RunProgressEntry` to `OptimizationStatusResponse`, (3) if not found, checks `IRunRepository.GetOptimizationByIdAsync()` for completed run and returns Completed status with full result, (4) if neither found, returns 404 in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`
- [X] T037 [US2] Write integration tests for optimization submission and status endpoints: verify POST returns 202; verify dedup returns same ID; verify GET /status returns progress while running and full result when completed; verify GET /status for unknown ID returns 404 in `tests/AlgoTradeForge.WebApi.Tests/Endpoints/OptimizationEndpointsTests.cs`
- [X] T038 [US2] Verify US2 end-to-end: start WebApi, submit an optimization via curl, poll /status, confirm completion with full results and any failed trials showing error details

**Checkpoint**: Both async backtest and optimization flows work end-to-end via API with dedup.

---

## Phase 5: User Story 3 — Frontend Polls and Displays Progress (Priority: P2)

**Goal**: Frontend submits runs, automatically polls for progress, displays "Processed X / Total (Bars|Combinations)", shows full results on completion, and displays error details for failures

**Independent Test**: Open dashboard in browser, submit a backtest, observe progress display updating every 5 seconds, see full results on completion; repeat for optimization with 30-second polling

### Implementation for User Story 3

- [X] T039 [US3] Create polling hooks `useBacktestStatus(id)` and `useOptimizationStatus(id)` using TanStack Query with `refetchInterval` that returns `false` when status is terminal (Completed/Failed/Cancelled). Backtest polls every 5000ms, optimization every 30000ms in `frontend/hooks/use-run-status.ts`
- [X] T040 [US3] Create `RunProgress` component that displays "Processed X / Total Bars" or "Processed X / Total Combinations" with a progress bar/indicator, handles error/failed/cancelled states, and shows full results when completed in `frontend/components/features/dashboard/run-progress.tsx`
- [X] T041 [US3] Update `RunNewPanel` to: (1) on successful submission, extract run ID from the 202 response, (2) instead of closing the panel, switch to showing the `RunProgress` component with the polling hook active, (3) on completion, call `onSuccess()` to refresh the runs list and navigate to the result in `frontend/components/features/dashboard/run-new-panel.tsx`
- [X] T042 [P] [US3] Update backtest report page to display `errorMessage` and `errorStackTrace` fields when present (using existing error panel pattern with `border-accent-red`) in `frontend/app/report/backtest/[id]/page.tsx`
- [X] T043 [P] [US3] Update optimization report page to display error details for failed trials in the trials table (show error icon/indicator and expandable error message + stacktrace) in `frontend/app/report/optimization/[id]/page.tsx`
- [X] T044 [US3] Verify US3 end-to-end: start both backend and frontend, submit a backtest from the dashboard, observe progress updating automatically, confirm results display on completion; repeat for optimization

**Checkpoint**: Full frontend polling experience works. Users can submit runs and watch progress without manual refresh.

---

## Phase 6: User Story 4 — Cancel a Running Operation (Priority: P3)

**Goal**: Users can cancel in-progress backtests and optimizations; the system stops processing promptly and marks the run as cancelled. Cancel endpoints use `IRunCancellationRegistry.TryCancel()`.

**Independent Test**: Submit a long-running operation, send cancel request, verify status changes to Cancelled and processing stops within 10 seconds

### Tests for User Story 4

- [X] T045 [P] [US4] Write tests for cancel endpoints: verify POST /api/backtests/{id}/cancel calls `IRunCancellationRegistry.TryCancel()`, returns 200 with Cancelled status when run is Running or Pending; returns 409 Conflict when run is in terminal state (Completed/Failed); returns 404 when run ID not found in `RunProgressCache`; same cases for POST /api/optimizations/{id}/cancel in `tests/AlgoTradeForge.WebApi.Tests/Endpoints/CancelEndpointsTests.cs`
- [X] T046 [P] [US4] Write tests for cancellation handling in command handlers: verify `RunBacktestCommandHandler` catches `OperationCanceledException` and updates cache entry status to Cancelled (not Failed), removes RunKey mapping and CTS; verify `RunOptimizationCommandHandler` catches `OperationCanceledException` from `Parallel.ForEachAsync` and updates cache status to Cancelled, removes RunKey and CTS in `tests/AlgoTradeForge.Application.Tests/Backtests/RunBacktestCommandHandlerCancellationTests.cs` and `tests/AlgoTradeForge.Application.Tests/Optimization/RunOptimizationCommandHandlerCancellationTests.cs`

### Implementation for User Story 4

- [X] T047 [US4] Add `POST /api/backtests/{id}/cancel` endpoint that: (1) looks up `RunProgressEntry` in `RunProgressCache`, (2) if found and status is Running/Pending, calls `IRunCancellationRegistry.TryCancel(id)` and updates cache entry status to Cancelled, returns 200 with `{id, status}`, (3) if terminal state, returns 409 Conflict, (4) if not found in cache, returns 404 in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`
- [X] T048 [US4] Add `POST /api/optimizations/{id}/cancel` endpoint with same logic as backtest cancel, using `RunProgressCache` + `IRunCancellationRegistry.TryCancel()` in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`
- [X] T049 [US4] Ensure `RunBacktestCommandHandler` background task catches `OperationCanceledException` from `ct.ThrowIfCancellationRequested()` in the engine loop and updates cache entry status to Cancelled (not Failed), removes RunKey mapping, removes CTS in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`
- [X] T050 [US4] Ensure `RunOptimizationCommandHandler` background task catches `OperationCanceledException` from the `Parallel.ForEachAsync` cancellation and updates cache entry status to Cancelled (not Failed), removes RunKey mapping, removes CTS in `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs`
- [X] T051 [US4] Add cancel button to the `RunProgress` frontend component that calls `cancelBacktest(id)` or `cancelOptimization(id)` and updates UI to show Cancelled state in `frontend/components/features/dashboard/run-progress.tsx`
- [X] T052 [US4] Verify US4 end-to-end: submit a long-running operation, click cancel, verify status changes to Cancelled and processing stops

**Checkpoint**: Full cancellation flow works end-to-end from frontend through backend.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, cleanup, and edge case handling

- [X] T053 Add structured logging (Serilog) to background processing in both `RunBacktestCommandHandler` and `RunOptimizationCommandHandler`: log at Information level on run start/completion, Warning on per-trial failures, Error on full run failures, with run ID and timing context per Constitution Principle IV (Observability)
- [X] T054 Ensure progress cache entries and RunKey mappings are cleaned up after completed/failed/cancelled runs are persisted — call `RunProgressCache.RemoveAsync(id)` and `RunProgressCache.RemoveRunKeyAsync(runKey)` after a 60-second `Task.Delay` (hardcoded constant; allows final polls to read terminal state before eviction) + verify `IRunCancellationRegistry.Remove(id)` is called immediately in both handlers (CTS is not needed after completion)
- [X] T055 Run the full test suite (`dotnet test AlgoTradeForge.slnx`) and fix any regressions
- [X] T056 Run quickstart.md validation scenario end-to-end (backend API + frontend + polling + results)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Foundational (Phase 2)
- **US2 (Phase 4)**: Depends on Foundational (Phase 2) — can run in PARALLEL with US1
- **US3 (Phase 5)**: Depends on US1 and US2 (needs backend status endpoints to poll)
- **US4 (Phase 6)**: Depends on US1 and US2 (needs running operations to cancel)
- **Polish (Phase 7)**: Depends on all previous phases

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2 — no dependency on other stories
- **US2 (P1)**: Can start after Phase 2 — no dependency on other stories; **can run in parallel with US1**
- **US3 (P2)**: Requires US1 and US2 backend endpoints to exist (needs status endpoints to poll against)
- **US4 (P3)**: Requires US1 and US2 handler refactors to exist (needs CancellationTokenSource wired in handlers)

### Within Each User Story

- Tests FIRST → then models/types → then services/handlers → then endpoints → then integration verification
- Core implementation before edge case handling

### Parallel Opportunities

- T005, T006, T007, T008 (foundational tests) can run in parallel
- T009, T010 (enum + progress entry) can run in parallel
- T013, T014 (cancellation interface + impl) can run in parallel
- T019, T020 (submission DTOs) can run in parallel
- T021, T022, T023, T024, T025 (contracts + types + frontend) can run in parallel
- **US1 and US2 (Phases 3–4) can run in parallel** after Phase 2
- T042, T043 (report page error displays) can run in parallel
- T045, T046 (cancel tests) can run in parallel

---

## Parallel Example: Foundational Phase

```
# Launch foundational tests in parallel:
T005: "Write RunKeyBuilder tests"
T006: "Write RunProgressCache tests"
T007: "Write InMemoryRunCancellationRegistry tests"
T008: "Write ProgressTrackingEventBusSink tests"

# Launch new types in parallel (after tests written):
T009: "Create RunStatus enum"
T010: "Create RunProgressEntry record"

# Sequential (depends on RunStatus + RunProgressEntry):
T011: "Create RunKeyBuilder"
T012: "Create RunProgressCache"

# Launch cancellation types in parallel:
T013: "Create IRunCancellationRegistry interface"
T014: "Create InMemoryRunCancellationRegistry"

# DI registration (after types exist):
T015: "Register RunProgressCache + IRunCancellationRegistry in DI"

# Launch contracts + sink + DTOs + frontend in parallel:
T016: "Add error fields to BacktestRunRecord"
T017: "Update SqliteRunRepository for error columns"
T018: "Create ProgressTrackingEventBusSink"
T019: "Create BacktestSubmissionDto"
T020: "Create OptimizationSubmissionDto"
T021: "Create submission response records"
T022: "Create status response records"
T023: "Add error fields to BacktestRunResponse"
T024: "Add TypeScript types"
T025: "Add API client methods"
```

## Parallel Example: US1 + US2

```
# After Phase 2 is complete, launch both stories in parallel:

# US1 Track:
T027: "Write RunBacktestCommandHandler tests (with dedup)"
T028: "Refactor RunBacktestCommandHandler (RunProgressCache + RunKey + CTS)"
T029: "Modify POST backtest endpoint to 202"
T030: "Add GET backtest status endpoint"
T031: "Write backtest endpoint integration tests"
T032: "Verify US1 end-to-end"

# US2 Track (simultaneously):
T033: "Write RunOptimizationCommandHandler tests (with dedup)"
T034: "Refactor RunOptimizationCommandHandler (RunProgressCache + RunKey + CTS)"
T035: "Modify POST optimization endpoint to 202"
T036: "Add GET optimization status endpoint"
T037: "Write optimization endpoint integration tests"
T038: "Verify US2 end-to-end"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (async backtest with dedup)
4. **STOP and VALIDATE**: Test US1 via curl against running API
5. Backtest operations no longer timeout — core value delivered

### Incremental Delivery

1. Setup + Foundational → Infrastructure ready
2. US1 (async backtest + dedup) → Test via API → Core MVP
3. US2 (async optimization + dedup) → Test via API → Both operations async
4. US3 (frontend progress) → Test in browser → User-facing experience complete
5. US4 (cancellation) → Test in browser → Full feature delivered
6. Polish → Final validation

### Parallel Team Strategy

With two developers after Phase 2:

- **Developer A**: US1 (async backtest) → US3 frontend backtest parts
- **Developer B**: US2 (async optimization) → US3 frontend optimization parts
- Then: US4 cancellation + Polish together

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- US1 and US2 are both P1 and can run in parallel after foundational phase
- US3 (frontend) depends on both US1 and US2 backend endpoints existing
- Constitution Principle II requires tests before implementation — test tasks precede implementation in each phase
- Constitution v1.6.0 requires RunKey dedup via `IDistributedCache` — covered by RunKeyBuilder + RunProgressCache (T005-T006, T011-T012)
- `IDistributedCache` registered via `AddDistributedMemoryCache()` (T004); swappable to Redis via DI
- `CancellationTokenSource` stored in `IRunCancellationRegistry` (not serializable, cannot use cache) — T007, T013-T014
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
