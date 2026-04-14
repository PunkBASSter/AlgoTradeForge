# Tasks: Optimization Task Queue

**Input**: Design documents from `specs/029-optimization-task-queue/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/queue-api.md

**Tests**: Included in Phase 7 (Polish) as the plan specifies test files.

**Organization**: Tasks grouped by user story for independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story (US1, US2, US3, US4)
- Includes exact file paths

---

## Phase 1: Setup

**Purpose**: No new projects needed. Verify build baseline.

- [x] T001 Verify clean build of `AlgoTradeForge.slnx` and confirm existing tests pass before starting changes

---

## Phase 2: Foundational (Blocking Pre-requisites)

**Purpose**: Core queue types and infrastructure that ALL user stories depend on.

**CRITICAL**: No user story work can begin until this phase is complete.

- [x] T002 [P] Create `ComputeTaskType` enum and `ComputeTaskStatus` enum in `src/AlgoTradeForge.Application/Optimization/ComputeTask.cs` — types: Optimization, Validation; statuses: Pending, InProgress, Completed, Failed, Cancelled
- [x] T003 [P] Create `ComputeTask` record in `src/AlgoTradeForge.Application/Optimization/ComputeTask.cs` — fields: Id (Guid), JobId, Type, DssIndex, RunId, Status, DssLabel, ErrorMessage, EnqueuedAt; per data-model.md
- [x] T004 Create `ComputeTaskQueue` singleton class in `src/AlgoTradeForge.Application/Optimization/ComputeTaskQueue.cs` — wraps `Channel<ComputeTask>.CreateUnbounded(new UnboundedChannelOptions { SingleReader = true })` + `ConcurrentDictionary<Guid, ComputeTask>` for task lookup + `ActiveTask` property. Implement: `Enqueue()`, `EnqueueRange()`, `TryCancelTask()`, `PurgePending()`, `GetSnapshot()`, `RemoveCompleted()`. Follow existing `Channel<T>` pattern from `src/AlgoTradeForge.Infrastructure/Live/LiveOrderContext.cs`
- [x] T005 Register `ComputeTaskQueue` as singleton in `src/AlgoTradeForge.Application/DependencyInjection.cs`

**Checkpoint**: ComputeTask types and queue infrastructure ready — user story implementation can begin.

---

## Phase 3: User Story 1 — Submit Optimization with Automatic Validation (Priority: P1) MVP

**Goal**: Replace fire-and-forget execution with sequential queue processing. Optimization and validation tasks execute one at a time. Trial cache reused between optimization and validation for same DSS.

**Independent Test**: Submit a multi-DSS optimization with validation enabled. Verify tasks execute sequentially (opt#0, val#0, opt#1, val#1). Verify validation uses cached trial data (log message: "Using cached trials"). Verify DB persistence happens during handoff.

### Implementation for User Story 1

- [x] T006 [US1] Extract `OptimizationTaskExecutor` class in `src/AlgoTradeForge.Application/Optimization/OptimizationTaskExecutor.cs` — move `RunSingleDssBruteForceAsync()` logic from `RunGroupOptimizationCommandHandler` (data loading, parallel trial evaluation via Partitioner, BoundedTrialQueue collection, progress updates). Accept `maxParallelism` parameter. Return trial results (BoundedTrialQueue + metrics + strategy version) for cache handoff. Inject same dependencies as handler: IOptimizationStrategyFactory, OptimizationSetupHelper, RunProgressCache, IRunCancellationRegistry, IRunRepository, ILogger
- [x] T007 [US1] Extract `ValidationTaskExecutor` class in `src/AlgoTradeForge.Application/Validation/ValidationTaskExecutor.cs` — move `RunSingleValidationAsync()` logic from `RunGroupValidationCommandHandler` (trial loading, SimulationCache building, ValidationPipeline execution, composite score calculation). Accept optional `IReadOnlyList<BacktestRunRecord>` parameter for cached trials — when non-null, skip `runRepository.GetOptimizationByIdAsync()` DB load. Fall back to DB load when null (standalone validation). Inject: IRunRepository, IValidationRepository, RunProgressCache, ISimulationCacheFileStore, IOptions<SimulationCacheOptions>, ILogger
- [x] T008 [US1] Create `ComputeQueueConsumer` BackgroundService in `src/AlgoTradeForge.WebApi/ComputeQueueConsumer.cs` — inject ComputeTaskQueue, OptimizationTaskExecutor, ValidationTaskExecutor, RunProgressCache, IRunCancellationRegistry, IRunRepository, IValidationRepository, ILogger. In `ExecuteAsync()`: `await foreach (var task in queue.Channel.Reader.ReadAllAsync(stoppingToken))` → skip Cancelled tasks → set InProgress → call appropriate executor → manage per-DSS trial cache via `Dictionary<(Guid JobId, int DssIndex), IReadOnlyList<BacktestRunRecord>>` → fire-and-forget `Task.Run(SaveOptimizationAsync)` between opt→val transitions → release cache after validation completes → handle exceptions (set Failed, cascade-cancel related validation tasks per FR-008) → register/deregister CTS in cancellation registry
- [x] T009 [US1] Refactor `RunGroupOptimizationCommandHandler.HandleAsync()` in `src/AlgoTradeForge.Application/Optimization/RunGroupOptimizationCommandHandler.cs` — keep: dedup check, strategy validation, axis resolution, combination estimation, DB placeholder insertion. Remove: `Task.Factory.StartNew(() => RunGroupBruteForceAsync(...))` and the `RunGroupBruteForceAsync` method. Add: create ComputeTask records for each DSS (optimization + optional validation if command.Validate is true), call `queue.EnqueueRange()`. Add `Validate`, `ThresholdProfileName` properties to `RunGroupOptimizationCommand`
- [x] T010 [US1] Refactor `RunOptimizationCommandHandler` in `src/AlgoTradeForge.Application/Optimization/RunOptimizationCommandHandler.cs` — delegate to RunGroupOptimizationCommandHandler by wrapping single-DSS as a group of 1. Remove `RunOptimizationAsync()` method and `Task.Factory.StartNew()` call
- [ ] T011 [US1] [DEFERRED] Refactor `RunGeneticOptimizationCommandHandler` in `src/AlgoTradeForge.Application/Optimization/RunGeneticOptimizationCommandHandler.cs` — keep: genetic config setup, population initialization, placeholder insertion. Remove: `Task.Factory.StartNew()`. Add: enqueue 1 optimization task (+ optional 1 validation task if Validate=true) via `queue.EnqueueRange()`
- [x] T012 [US1] Refactor `RunGroupValidationCommandHandler.HandleAsync()` in `src/AlgoTradeForge.Application/Validation/RunGroupValidationCommandHandler.cs` — keep: load optimization group, validate completed runs, resolve threshold profile, insert validation group + child placeholders. Remove: `Task.Factory.StartNew(() => RunGroupSequentialAsync(...))` and the `RunGroupSequentialAsync` method. Add: create ComputeTask records (type=Validation) for each completed DSS, call `queue.EnqueueRange()`
- [x] T013 [US1] Refactor `RunValidationCommandHandler` in `src/AlgoTradeForge.Application/Validation/RunValidationCommandHandler.cs` — delegate to RunGroupValidationCommandHandler by wrapping single validation as a group of 1. Remove `RunValidationAsync()` method and `Task.Factory.StartNew()` call
- [x] T014 [US1] Register `OptimizationTaskExecutor` and `ValidationTaskExecutor` as singletons in `src/AlgoTradeForge.Application/DependencyInjection.cs`. Register `ComputeQueueConsumer` as hosted service via `builder.Services.AddHostedService<ComputeQueueConsumer>()` in `src/AlgoTradeForge.WebApi/Program.cs`
- [x] T015 [US1] Add `Validate` (bool, default false), `ThresholdProfileName` (string, default "Crypto-Standard"), and `MaxThreads` (int, default 0) properties to `RunOptimizationRequest` in `src/AlgoTradeForge.WebApi/Contracts/RunOptimizationRequest.cs`. Wire these fields through to `RunGroupOptimizationCommand` in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs`. Add `EnqueuedTasks` to `OptimizationSubmissionResponse`
- [x] T016 [US1] Wire validation endpoint `POST /api/validations/groups` in `src/AlgoTradeForge.WebApi/Endpoints/ValidationEndpoints.cs` — ensure it calls the refactored handler that enqueues through the queue. Add `EnqueuedTasks` to response

**Checkpoint**: Core queue working — optimization and validation tasks execute sequentially, trial cache reused between phases, DB persistence during handoff. Verify with `dotnet build` + manual test.

---

## Phase 4: User Story 2 — Monitor Task Queue Progress (Priority: P2)

**Goal**: Expose queue state via API and display in a frontend task queue panel with real-time polling.

**Independent Test**: Submit an optimization, open the UI, verify the task queue panel shows pending and in-progress tasks with progress (combination counts for optimization, stage counts for validation). Verify tasks disappear from queue on completion.

### Implementation for User Story 2

- [ ] T017 [P] [US2] Create `TaskQueueContracts.cs` in `src/AlgoTradeForge.WebApi/Contracts/TaskQueueContracts.cs` — DTOs: `TaskQueueItemResponse` (id, jobId, type, dssIndex, dssLabel, runId, status, enqueuedAt, progress), `TaskQueueSnapshotResponse` (activeTasks, pendingCount, inProgressTask), `TaskProgressDto` (processed, total). Per contracts/queue-api.md
- [ ] T018 [US2] Create `TaskQueueEndpoints.cs` in `src/AlgoTradeForge.WebApi/Endpoints/TaskQueueEndpoints.cs` — `GET /api/queue` endpoint: inject ComputeTaskQueue and RunProgressCache, call `queue.GetSnapshot()`, enrich in-progress task with progress from `progressCache.GetProgressAsync(task.RunId)`, return `TaskQueueSnapshotResponse`. Register endpoint group in `src/AlgoTradeForge.WebApi/Program.cs`
- [ ] T019 [P] [US2] Create TypeScript types in `frontend/types/task-queue.ts` — interfaces: `TaskQueueItem`, `TaskQueueSnapshot`, `TaskProgressDto`, `CancelTaskResponse`, `PurgeResponse` per contracts/queue-api.md
- [ ] T020 [US2] Add queue API functions to `frontend/lib/services/api-client.ts` — `getTaskQueue(): Promise<TaskQueueSnapshot>`, `cancelTask(taskId: string): Promise<CancelTaskResponse>`, `purgeQueue(): Promise<PurgeResponse>`
- [ ] T021 [US2] Create `useTaskQueue()` hook in `frontend/hooks/use-task-queue.ts` — TanStack Query `useQuery` with `queryKey: ["task-queue"]`, `refetchInterval: 2000` (2s polling). Stop polling when snapshot has zero tasks. Follow existing pattern from `frontend/hooks/use-optimization-groups.ts`
- [ ] T022 [US2] Create `TaskQueuePanel` component in `frontend/components/features/dashboard/task-queue-panel.tsx` — display pending + in-progress tasks with: StatusBadge (reuse from `frontend/components/ui/status-badge.tsx`), DSS label, task type, progress bar (combination counts for optimization, stage/total for validation). Render nothing when queue is empty. Follow existing component patterns

**Checkpoint**: Queue status visible in UI with 2s polling updates. Completed tasks disappear from panel.

---

## Phase 5: User Story 3 — Cancel Individual Tasks and Purge Pending (Priority: P2)

**Goal**: Users can cancel individual tasks (pending or in-progress) and purge all pending tasks from the queue.

**Independent Test**: Submit multi-DSS optimization, cancel one pending task, verify it's removed and remaining tasks continue. Cancel in-progress task, verify it stops. Purge pending, verify all pending cleared. Verify optimization cancellation cascades to paired validation task.

### Implementation for User Story 3

- [ ] T023 [US3] Add cancel endpoint `POST /api/queue/{taskId}/cancel` to `src/AlgoTradeForge.WebApi/Endpoints/TaskQueueEndpoints.cs` — call `queue.TryCancelTask(taskId)`, implement cascade cancellation (find pending validation tasks with same JobId+DssIndex, cancel them, update DB records), return `CancelTaskResponse` with cascadeCancelled list. Return 404 if task not in queue, 409 if already terminal
- [ ] T024 [US3] Add purge endpoint `POST /api/queue/purge` to `src/AlgoTradeForge.WebApi/Endpoints/TaskQueueEndpoints.cs` — call `queue.PurgePending()`, update DB records for purged tasks to Cancelled status, return `PurgeResponse` with purgedCount and purgedTaskIds
- [ ] T025 [US3] Add cancel button per task and "Purge Pending" button to `TaskQueuePanel` in `frontend/components/features/dashboard/task-queue-panel.tsx` — cancel button calls `cancelTask(taskId)` mutation, purge button calls `purgeQueue()` mutation. Both invalidate `["task-queue"]` query on success. Show confirmation dialog for purge. Disable cancel button on terminal states

**Checkpoint**: Cancel and purge working end-to-end. Cascade cancellation verified.

---

## Phase 6: User Story 4 — Configure Max Threads (Priority: P3)

**Goal**: Users control parallelism within each compute task via a max threads parameter.

**Independent Test**: Submit optimization with max threads = 2, verify executor uses 2 parallel workers (check logs for partition count). Submit with 0, verify it uses CPU core count.

### Implementation for User Story 4

- [ ] T026 [US4] Implement max threads clamping in `OptimizationTaskExecutor` in `src/AlgoTradeForge.Application/Optimization/OptimizationTaskExecutor.cs` — when maxParallelism is 0, use `Environment.ProcessorCount`; when positive, clamp to `Math.Min(value, Environment.ProcessorCount)`. Pass to `Partitioner.Create()` partition count
- [ ] T027 [US4] Add max threads input to optimization form in `frontend/components/features/dashboard/run-new-panel.tsx` — integer input with label "Max Threads", default 0, min 0, tooltip "0 = use all CPU cores". Add validate checkbox with label "Run Validation". Add threshold profile dropdown (populate from existing `getThresholdProfiles()` API, shown only when validate is checked). Wire all three fields into the JSON template that gets submitted

**Checkpoint**: Max threads, validate toggle, and threshold profile controls working end-to-end.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Tests, edge cases, and cleanup across all user stories.

- [ ] T028 [P] Create `ComputeTaskQueueTests` in `tests/AlgoTradeForge.Application.Tests/Optimization/ComputeTaskQueueTests.cs` — test: Enqueue adds to channel and dictionary, TryCancelTask sets pending→cancelled, TryCancelTask on in-progress signals cancellation, PurgePending cancels all pending and returns count, GetSnapshot returns ordered list (in-progress first), cascade cancellation from optimization to paired validation (FR-008), RemoveCompleted cleans up dictionary
- [ ] T029 [P] Create `OptimizationTaskExecutorTests` in `tests/AlgoTradeForge.Application.Tests/Optimization/OptimizationTaskExecutorTests.cs` — test: executor runs with specified maxParallelism, executor returns trial results for cache handoff, executor respects cancellation token
- [ ] T030 [P] Create `ValidationTaskExecutorTests` in `tests/AlgoTradeForge.Application.Tests/Validation/ValidationTaskExecutorTests.cs` — test: executor uses cached trials when provided (skips DB load), executor falls back to DB load when cache is null (standalone validation), executor handles zero trials gracefully (completes with empty result)
- [ ] T031 Handle edge case: zero passing trials in `ComputeQueueConsumer` in `src/AlgoTradeForge.WebApi/ComputeQueueConsumer.cs` — when optimization produces zero trials, pass empty list to validation task. Validation executor should complete immediately with empty result and "no candidates" verdict
- [ ] T032 Handle edge case: application restart in `ComputeQueueConsumer` in `src/AlgoTradeForge.WebApi/ComputeQueueConsumer.cs` — on startup, log warning about lost queue state. The ephemeral queue resets naturally; no recovery needed. Document this behavior in structured log
- [ ] T033 Verify build succeeds with `dotnet build AlgoTradeForge.slnx` and all tests pass with `dotnet test tests/AlgoTradeForge.Application.Tests/`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — verify baseline
- **Foundational (Phase 2)**: Depends on Phase 1 — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Phase 2 — core queue infrastructure
- **US2 (Phase 4)**: Depends on Phase 3 — needs queue to have tasks to display
- **US3 (Phase 5)**: Depends on Phase 4 — cancel/purge UI extends the queue panel
- **US4 (Phase 6)**: Depends on Phase 3 only — max threads is independent of UI
- **Polish (Phase 7)**: Depends on all user stories being complete

### User Story Dependencies

- **US1 (P1)**: Depends on Foundational (Phase 2) only — can start immediately after foundational
- **US2 (P2)**: Depends on US1 — queue must exist and process tasks for the UI to display
- **US3 (P2)**: Depends on US2 — cancel/purge buttons are on the queue panel
- **US4 (P3)**: Depends on US1 only — max threads is a parameter passed to the executor

### Within Each User Story

- Types/records before services
- Services before endpoints
- Backend before frontend
- Core implementation before integration

### Parallel Opportunities

- T002 + T003 (foundational types) can run in parallel
- T017 + T019 (backend DTOs + frontend types) can run in parallel
- T028 + T029 + T030 (all test files) can run in parallel
- US4 (Phase 6) can run in parallel with US2+US3 (Phases 4-5) since it only depends on US1

---

## Parallel Example: User Story 1

```
# These tasks are sequential (each depends on prior):
T006: Extract OptimizationTaskExecutor
T007: Extract ValidationTaskExecutor
T008: Create ComputeQueueConsumer (depends on T006, T007)
T009-T013: Refactor handlers (depends on T008)
T014: Register services (depends on T006-T013)
T015-T016: Wire request/response changes (depends on T014)
```

## Parallel Example: User Story 2

```
# Launch backend + frontend types in parallel:
T017: Create TaskQueueContracts.cs (backend DTOs)
T019: Create task-queue.ts (frontend types)

# Then sequentially:
T018: Create TaskQueueEndpoints.cs (depends on T017)
T020: Add API functions to api-client.ts (depends on T019)
T021: Create useTaskQueue hook (depends on T020)
T022: Create TaskQueuePanel component (depends on T021)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (verify build)
2. Complete Phase 2: Foundational (ComputeTask types + ComputeTaskQueue)
3. Complete Phase 3: User Story 1 (executors, consumer, handler refactoring)
4. **STOP and VALIDATE**: Test queue execution manually — submit multi-DSS optimization, verify sequential processing, verify trial cache reuse
5. Backend is fully functional at this point; all existing API contracts preserved

### Incremental Delivery

1. Phase 1-2 → Foundation ready
2. Phase 3 (US1) → Core queue working → Backend MVP
3. Phase 4 (US2) → Queue visible in UI → Transparency achieved
4. Phase 5 (US3) → Cancel/purge controls → User control achieved
5. Phase 6 (US4) → Max threads → Resource control achieved
6. Phase 7 → Tests + edge cases → Production ready

---

## Notes

- [P] tasks = different files, no dependencies
- [USx] label maps task to specific user story for traceability
- Existing handler tests will need updating after refactoring (Phases 3) — verify in Phase 7
- No new NuGet packages — all implementation uses existing framework types
- Constitution-aligned: no external broker, no new DB tables, ephemeral queue
