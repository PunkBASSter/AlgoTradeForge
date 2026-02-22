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

**Purpose**: Verify build and create folder structure for new types

- [ ] T001 Verify solution builds successfully and all existing tests pass by running `dotnet build AlgoTradeForge.slnx` and `dotnet test AlgoTradeForge.slnx`
- [ ] T002 Create `Progress/` folder under `src/AlgoTradeForge.Application/Progress/`
- [ ] T003 Verify `tests/AlgoTradeForge.Application.Tests/` project exists; if not, create it with `dotnet new xunit`, add project references to `AlgoTradeForge.Application`, add NSubstitute package, and add it to the solution `AlgoTradeForge.slnx`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core types and infrastructure that ALL user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete

### Tests for Foundational

- [ ] T004 [P] Write unit tests for `InMemoryRunProgressStore` (register, get, remove for both backtest and optimization; thread-safety; not-found returns null) in `tests/AlgoTradeForge.Application.Tests/Progress/InMemoryRunProgressStoreTests.cs`
- [ ] T005 [P] Write unit tests for `ProgressTrackingEventBusSink` (increments ProcessedBars on BarEvent, ignores other event types) in `tests/AlgoTradeForge.Application.Tests/Backtests/ProgressTrackingEventBusSinkTests.cs`

### Implementation for Foundational

- [ ] T006 [P] Create `RunStatus` enum (Pending, Running, Completed, Failed, Cancelled) in `src/AlgoTradeForge.Application/Progress/RunStatus.cs`
- [ ] T007 [P] Create `BacktestProgress` class with fields per data-model.md (Id, Status, TotalBars, ProcessedBars, ErrorMessage, ErrorStackTrace, Result, CancellationTokenSource, StartedAt) in `src/AlgoTradeForge.Application/Progress/BacktestProgress.cs`
- [ ] T008 [P] Create `OptimizationProgress` class with fields per data-model.md (Id, Status, TotalCombinations, CompletedCombinations, FailedCombinations, ErrorMessage, ErrorStackTrace, Result, CancellationTokenSource, StartedAt) in `src/AlgoTradeForge.Application/Progress/OptimizationProgress.cs`
- [ ] T009 Create `IRunProgressStore` interface with Register/Get/Remove methods for both backtest and optimization in `src/AlgoTradeForge.Application/Progress/IRunProgressStore.cs`
- [ ] T010 Implement `InMemoryRunProgressStore` using two `ConcurrentDictionary<Guid, T>` instances (following `InMemoryDebugSessionStore` pattern) in `src/AlgoTradeForge.Application/Progress/InMemoryRunProgressStore.cs`
- [ ] T011 Add `ErrorMessage` (string?) and `ErrorStackTrace` (string?) fields to `BacktestRunRecord` in `src/AlgoTradeForge.Application/Persistence/BacktestRunRecord.cs`
- [ ] T012 Update `SqliteRunRepository` to read/write `error_message` and `error_stack_trace` columns in INSERT and SELECT queries for `backtest_runs` table. Add the two nullable TEXT columns in the schema initialization in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs`
- [ ] T013 Register `IRunProgressStore` as Singleton (`InMemoryRunProgressStore`) in `src/AlgoTradeForge.Application/DependencyInjection.cs`
- [ ] T014 [P] Create `BacktestSubmissionResponse` and `OptimizationSubmissionResponse` records per data-model.md in `src/AlgoTradeForge.WebApi/Contracts/SubmissionResponses.cs`
- [ ] T015 [P] Create `BacktestStatusResponse` and `OptimizationStatusResponse` records per data-model.md (with nullable Result field) in `src/AlgoTradeForge.WebApi/Contracts/StatusResponses.cs`
- [ ] T016 [P] Add `ErrorMessage` (string?) and `ErrorStackTrace` (string?) fields to `BacktestRunResponse` in `src/AlgoTradeForge.WebApi/Contracts/RunContracts.cs` and update the mapping from `BacktestRunRecord`
- [ ] T017 Create `ProgressTrackingEventBusSink` implementing `ISink` that increments `BacktestProgress.ProcessedBars` via `Interlocked.Increment` on each `BarEvent` in `src/AlgoTradeForge.Application/Backtests/ProgressTrackingEventBusSink.cs`
- [ ] T018 [P] Add frontend TypeScript types: `BacktestSubmission`, `OptimizationSubmission`, `BacktestStatus`, `OptimizationStatus`, `RunStatusType` union, and add `errorMessage?`/`errorStackTrace?` to `BacktestRun` in `frontend/types/api.ts`
- [ ] T019 [P] Add API client methods: `getBacktestStatus(id)`, `getOptimizationStatus(id)`, `cancelBacktest(id)`, `cancelOptimization(id)`, and update `runBacktest()` return type to `BacktestSubmission` and `runOptimization()` return type to `OptimizationSubmission` in `frontend/lib/services/api-client.ts`
- [ ] T020 Verify all foundational tests pass by running `dotnet test AlgoTradeForge.slnx`

**Checkpoint**: All foundational types, contracts, and infrastructure ready. User story implementation can begin.

---

## Phase 3: User Story 1 — Submit Backtest and Track Progress (Priority: P1) MVP

**Goal**: POST /api/backtests/ returns 202 with run ID immediately; backtest runs in background; GET /api/backtests/{id}/status returns progress and results

**Independent Test**: Submit a backtest via curl, verify 202 response with run ID, poll /status until completion, confirm final results match previous synchronous output

### Tests for User Story 1

- [ ] T021 [US1] Write tests for refactored `RunBacktestCommandHandler`: verify it registers progress in store, starts background task, returns submission response with ID and total bars; verify on completion it persists results and updates progress store with completed status and result. Use NSubstitute for dependencies in `tests/AlgoTradeForge.Application.Tests/Backtests/RunBacktestCommandHandlerTests.cs`

### Implementation for User Story 1

- [ ] T022 [US1] Refactor `RunBacktestCommandHandler` to: (1) inject `IRunProgressStore`, (2) perform validation and data loading synchronously (prepare backtest setup, get total bar count), (3) create `BacktestProgress` with Pending status, register in store, (4) start `Task.Run()` with the engine.Run execution + progress tracking via `ProgressTrackingEventBusSink` + result persistence + progress store update on completion/failure, (5) return submission data (ID, totalBars) immediately. Change the handler's `TResult` generic parameter from `BacktestResultDto` to a new `BacktestSubmissionDto` record (containing Id and TotalBars) in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`
- [ ] T023 [US1] Modify POST /api/backtests/ endpoint to return `Results.Accepted(BacktestSubmissionResponse)` (HTTP 202) instead of `Results.Ok(BacktestResultDto)` in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`
- [ ] T024 [US1] Add `GET /api/backtests/{id}/status` endpoint that: (1) checks `IRunProgressStore` for in-progress backtest, (2) if found, maps `BacktestProgress` to `BacktestStatusResponse`, (3) if not found, checks `IRunRepository.GetByIdAsync()` for completed run and returns Completed status with full result, (4) if neither found, returns 404 in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`
- [ ] T025 [US1] Write integration tests for backtest status and submission endpoints: verify POST /api/backtests/ returns 202 with BacktestSubmissionResponse; verify GET /api/backtests/{id}/status returns progress while running and full result when completed; verify GET /api/backtests/{unknownId}/status returns 404 in `tests/AlgoTradeForge.WebApi.Tests/Endpoints/BacktestEndpointsTests.cs`
- [ ] T026 [US1] Verify US1 end-to-end: build solution, start WebApi, submit a backtest via curl, poll /status, confirm completion with full results matching expected format

**Checkpoint**: Async backtest flow works end-to-end via API. Frontend not yet updated.

---

## Phase 4: User Story 2 — Submit Optimization and Track Progress (Priority: P1)

**Goal**: POST /api/optimizations/ returns 202 with run ID immediately; optimization runs in background with per-trial error handling; GET /api/optimizations/{id}/status returns progress counts and results on completion

**Independent Test**: Submit an optimization via curl, verify 202 with run ID and totalCombinations, poll /status until completion, confirm final results match previous synchronous output including any failed trials with error details

### Tests for User Story 2

- [ ] T027 [US2] Write tests for refactored `RunOptimizationCommandHandler`: verify it registers progress in store, starts background task, returns submission data; verify per-trial error handling (failed trials saved with ErrorMessage/ErrorStackTrace, successful trials unaffected); verify on completion it persists results and updates progress store. Use NSubstitute for dependencies in `tests/AlgoTradeForge.Application.Tests/Optimization/RunOptimizationCommandHandlerTests.cs`

### Implementation for User Story 2

- [ ] T028 [US2] Refactor `RunOptimizationCommandHandler` to: (1) inject `IRunProgressStore`, (2) perform validation, axis resolution, combination estimation, and data pre-loading synchronously, (3) create `OptimizationProgress` with Pending status and totalCombinations, register in store, (4) start `Task.Run()` with the `Parallel.ForEachAsync` execution, wrapping each trial body in try/catch to save failed trials with error info (ErrorMessage, ErrorStackTrace) + `Interlocked.Increment` on CompletedCombinations or FailedCombinations + result persistence + progress store update on completion/failure, (5) return submission data (ID, totalCombinations) immediately in `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs`
- [ ] T029 [US2] Modify POST /api/optimizations/ endpoint to return `Results.Accepted(OptimizationSubmissionResponse)` (HTTP 202) in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`
- [ ] T030 [US2] Add `GET /api/optimizations/{id}/status` endpoint that: (1) checks `IRunProgressStore` for in-progress optimization, (2) if found, maps `OptimizationProgress` to `OptimizationStatusResponse`, (3) if not found, checks `IRunRepository.GetOptimizationByIdAsync()` for completed run and returns Completed status with full result, (4) if neither found, returns 404 in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`
- [ ] T031 [US2] Write integration tests for optimization status and submission endpoints: verify POST /api/optimizations/ returns 202 with OptimizationSubmissionResponse; verify GET /api/optimizations/{id}/status returns progress while running and full result when completed; verify GET /api/optimizations/{unknownId}/status returns 404 in `tests/AlgoTradeForge.WebApi.Tests/Endpoints/OptimizationEndpointsTests.cs`
- [ ] T032 [US2] Verify US2 end-to-end: start WebApi, submit an optimization via curl, poll /status, confirm completion with full results and any failed trials showing error details

**Checkpoint**: Both async backtest and optimization flows work end-to-end via API.

---

## Phase 5: User Story 3 — Frontend Polls and Displays Progress (Priority: P2)

**Goal**: Frontend submits runs, automatically polls for progress, displays "Processed X / Total (Bars|Combinations)", shows full results on completion, and displays error details for failures

**Independent Test**: Open dashboard in browser, submit a backtest, observe progress display updating every 5 seconds, see full results on completion; repeat for optimization with 30-second polling

### Implementation for User Story 3

- [ ] T033 [US3] Create polling hooks `useBacktestStatus(id)` and `useOptimizationStatus(id)` using TanStack Query with `refetchInterval` that returns `false` when status is terminal (Completed/Failed/Cancelled). Backtest polls every 5000ms, optimization every 30000ms in `frontend/hooks/use-run-status.ts`
- [ ] T034 [US3] Create `RunProgress` component that displays "Processed X / Total Bars" or "Processed X / Total Combinations" with a progress bar/indicator, handles error/failed/cancelled states, and shows full results when completed in `frontend/components/features/dashboard/run-progress.tsx`
- [ ] T035 [US3] Update `RunNewPanel` to: (1) on successful submission, extract run ID from the 202 response, (2) instead of closing the panel, switch to showing the `RunProgress` component with the polling hook active, (3) on completion, call `onSuccess()` to refresh the runs list and navigate to the result in `frontend/components/features/dashboard/run-new-panel.tsx`
- [ ] T036 [P] [US3] Update backtest report page to display `errorMessage` and `errorStackTrace` fields when present (using existing error panel pattern with `border-accent-red`) in `frontend/app/report/backtest/[id]/page.tsx`
- [ ] T037 [P] [US3] Update optimization report page to display error details for failed trials in the trials table (show error icon/indicator and expandable error message + stacktrace) in `frontend/app/report/optimization/[id]/page.tsx`
- [ ] T038 [US3] Verify US3 end-to-end: start both backend and frontend, submit a backtest from the dashboard, observe progress updating automatically, confirm results display on completion; repeat for optimization

**Checkpoint**: Full frontend polling experience works. Users can submit runs and watch progress without manual refresh.

---

## Phase 6: User Story 4 — Cancel a Running Operation (Priority: P3)

**Goal**: Users can cancel in-progress backtests and optimizations; the system stops processing promptly and marks the run as cancelled

**Independent Test**: Submit a long-running operation, send cancel request, verify status changes to Cancelled and processing stops within 10 seconds

### Tests for User Story 4

- [ ] T039 [P] [US4] Write tests for cancel endpoints: verify POST /api/backtests/{id}/cancel returns 200 with Cancelled status when run is Running or Pending, returns 409 Conflict when run is in terminal state (Completed/Failed), returns 404 when run ID not found; same cases for POST /api/optimizations/{id}/cancel in `tests/AlgoTradeForge.WebApi.Tests/Endpoints/CancelEndpointsTests.cs`
- [ ] T040 [P] [US4] Write tests for cancellation handling in command handlers: verify `RunBacktestCommandHandler` catches `OperationCanceledException` and sets progress status to Cancelled (not Failed); verify `RunOptimizationCommandHandler` catches `OperationCanceledException` from `Parallel.ForEachAsync` and sets status to Cancelled in `tests/AlgoTradeForge.Application.Tests/Backtests/RunBacktestCommandHandlerCancellationTests.cs` and `tests/AlgoTradeForge.Application.Tests/Optimization/RunOptimizationCommandHandlerCancellationTests.cs`

### Implementation for User Story 4

- [ ] T041 [US4] Add `POST /api/backtests/{id}/cancel` endpoint that: (1) looks up `BacktestProgress` in `IRunProgressStore`, (2) if found and status is Running/Pending, calls `CancellationTokenSource.Cancel()` and sets status to Cancelled, returns 200 with `{id, status}`, (3) if terminal state, returns 409 Conflict, (4) if not found, returns 404 in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs`
- [ ] T042 [US4] Add `POST /api/optimizations/{id}/cancel` endpoint with same logic as backtest cancel in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`
- [ ] T043 [US4] Ensure `RunBacktestCommandHandler` background task catches `OperationCanceledException` from `ct.ThrowIfCancellationRequested()` in the engine loop and sets progress status to Cancelled (not Failed) in `src/AlgoTradeForge.Application/Backtests/RunBacktestCommandHandler.cs`
- [ ] T044 [US4] Ensure `RunOptimizationCommandHandler` background task catches `OperationCanceledException` from the `Parallel.ForEachAsync` cancellation and sets progress status to Cancelled (not Failed) in `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs`
- [ ] T045 [US4] Add cancel button to the `RunProgress` frontend component that calls `cancelBacktest(id)` or `cancelOptimization(id)` and updates UI to show Cancelled state in `frontend/components/features/dashboard/run-progress.tsx`
- [ ] T046 [US4] Verify US4 end-to-end: submit a long-running operation, click cancel, verify status changes to Cancelled and processing stops

**Checkpoint**: Full cancellation flow works end-to-end from frontend through backend.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, cleanup, and edge case handling

- [ ] T047 Add structured logging (Serilog) to background processing in both `RunBacktestCommandHandler` and `RunOptimizationCommandHandler`: log at Information level on run start/completion, Warning on per-trial failures, Error on full run failures, with run ID and timing context per Constitution Principle IV (Observability)
- [ ] T048 Ensure progress store entries are cleaned up after completed/failed/cancelled runs are persisted (remove from `ConcurrentDictionary` after save to prevent unbounded memory growth) — verify in both `RunBacktestCommandHandler` and `RunOptimizationCommandHandler`
- [ ] T049 Run the full test suite (`dotnet test AlgoTradeForge.slnx`) and fix any regressions
- [ ] T050 Run quickstart.md validation scenario end-to-end (backend API + frontend + polling + results)

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

- T004, T005 (foundational tests) can run in parallel
- T006, T007, T008 (enum + progress classes) can run in parallel
- T014, T015, T016, T017 (contracts + sink) can run in parallel
- T018, T019 (frontend types + API client) can run in parallel
- **US1 and US2 (Phases 3–4) can run in parallel** after Phase 2
- T036, T037 (report page error displays) can run in parallel

---

## Parallel Example: Foundational Phase

```
# Launch foundational tests in parallel:
T004: "Write InMemoryRunProgressStore tests"
T005: "Write ProgressTrackingEventBusSink tests"

# Launch new types in parallel (after tests written):
T006: "Create RunStatus enum"
T007: "Create BacktestProgress class"
T008: "Create OptimizationProgress class"

# Launch contracts + sink in parallel:
T014: "Create submission response records"
T015: "Create status response records"
T016: "Add error fields to BacktestRunResponse"
T017: "Create ProgressTrackingEventBusSink"

# Launch frontend types in parallel:
T018: "Add TypeScript types"
T019: "Add API client methods"
```

## Parallel Example: US1 + US2

```
# After Phase 2 is complete, launch both stories in parallel:

# US1 Track:
T021: "Write RunBacktestCommandHandler tests"
T022: "Refactor RunBacktestCommandHandler"
T023: "Modify POST backtest endpoint to 202"
T024: "Add GET backtest status endpoint"
T025: "Write backtest endpoint integration tests"
T026: "Verify US1 end-to-end"

# US2 Track (simultaneously):
T027: "Write RunOptimizationCommandHandler tests"
T028: "Refactor RunOptimizationCommandHandler"
T029: "Modify POST optimization endpoint to 202"
T030: "Add GET optimization status endpoint"
T031: "Write optimization endpoint integration tests"
T032: "Verify US2 end-to-end"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (async backtest)
4. **STOP and VALIDATE**: Test US1 via curl against running API
5. Backtest operations no longer timeout — core value delivered

### Incremental Delivery

1. Setup + Foundational → Infrastructure ready
2. US1 (async backtest) → Test via API → Core MVP
3. US2 (async optimization) → Test via API → Both operations async
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
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
