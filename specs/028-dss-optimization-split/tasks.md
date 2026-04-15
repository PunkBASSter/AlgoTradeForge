# Tasks: Per-DSS Optimization Split

**Input**: Design documents from `/specs/028-dss-optimization-split/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/api-endpoints.md

**Tests**: Tests are included per constitution Principle II (Test-First).

**Organization**: Tasks grouped by user story. US1 is the MVP.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story (US1–US6)
- Exact file paths included

---

## Phase 1: Setup

**Purpose**: Schema changes, drop existing data, new tables

- [x] T001 Add `optimization_groups` table to schema in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs` — includes `status TEXT NOT NULL DEFAULT 'InProgress'`, `subscriptions_json`, `backtest_settings_json`, `optimization_settings_json`, `fitness_config_json`, `max_parallelism`
- [x] T002 Add `validation_groups` table to schema in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs` — includes `status`, `optimization_group_id` FK, `threshold_profile_name`
- [x] T003 Add `group_id TEXT NOT NULL` FK and `dss_index INTEGER NOT NULL` columns to `optimization_runs` table in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs`
- [x] T004 Add `validation_group_id TEXT NOT NULL` FK column to `validation_runs` table in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs`
- [x] T005 Add denormalized metric columns (`sharpe_ratio`, `sortino_ratio`, `profit_factor`, `max_drawdown_pct`, `win_rate_pct`, `total_trades`, `net_profit`, `annualized_return_pct`) to `backtest_runs` table in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs`
- [x] T006 Bump schema version to drop existing optimization/validation data (FR-014) and recreate all tables cleanly in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteDbInitializer.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Records, repositories, group infrastructure, and logging that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T007 [P] Create `OptimizationGroupRecord` in `src/AlgoTradeForge.Application/Persistence/OptimizationGroupRecord.cs` — fields per data-model.md: Id, StrategyName, StrategyVersion, OptimizationMethod, StartedAt, CompletedAt, TotalRuns, Status, InputJson, SubscriptionsJson, BacktestSettingsJson, OptimizationSettingsJson, FitnessConfigJson, MaxParallelism
- [x] T008 [P] Create `ValidationGroupRecord` in `src/AlgoTradeForge.Application/Persistence/ValidationGroupRecord.cs` — fields: Id, OptimizationGroupId, StrategyName, ThresholdProfileName, ThresholdProfileJson, StartedAt, CompletedAt, TotalRuns, Status
- [x] T009 [P] Create `GroupStatusCalculator` in `src/AlgoTradeForge.Application/Optimization/GroupStatusCalculator.cs` — static method that takes child run statuses and returns group status (InProgress/Completed/PartiallyCompleted/Failed/Cancelled) per derivation rules in data-model.md
- [x] T010 [P] Test `GroupStatusCalculator` in `tests/AlgoTradeForge.Application.Tests/Optimization/GroupStatusCalculatorTests.cs` — test all 5 status combinations: all completed, mixed, all failed, all cancelled, any in-progress
- [x] T011 Extend `RunKeyBuilder` with `BuildGroupKey()` method in `src/AlgoTradeForge.Application/Progress/RunKeyBuilder.cs` — hash of strategy + backtest settings + DSS list + axes + optimization method for group-level dedup
- [x] T012 Add group CRUD methods to `IRunRepository` in `src/AlgoTradeForge.Application/Repositories/IRunRepository.cs` — InsertOptimizationGroupAsync, GetOptimizationGroupByIdAsync, QueryOptimizationGroupsAsync, UpdateOptimizationGroupStatusAsync, DeleteOptimizationGroupAsync, GetOptimizationGroupTrialsAsync (cross-DSS sorted query)
- [x] T013 Add validation group CRUD methods to `IValidationRepository` in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteValidationRepository.cs` (implements existing `IValidationRepository` interface) — InsertValidationGroupAsync, GetValidationGroupByIdAsync, QueryValidationGroupsAsync, UpdateValidationGroupStatusAsync, DeleteValidationGroupAsync
- [x] T014 Implement optimization group repository methods in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs` — INSERT, SELECT with JOIN for child runs, UPDATE status, DELETE cascade, cross-DSS trials query using denormalized metric columns for ORDER BY
- [x] T015 Implement validation group repository methods in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteValidationRepository.cs` — same patterns as optimization group repository
- [ ] T016 [P] Test optimization group repository in `tests/AlgoTradeForge.Infrastructure.Tests/Persistence/SqliteRunRepository_GroupTests.cs` — insert group, insert child runs, query groups, cross-DSS trials sort, cascade delete
- [ ] T017 [P] Test validation group repository in `tests/AlgoTradeForge.Infrastructure.Tests/Persistence/SqliteValidationRepository_GroupTests.cs` — insert group, insert child runs, query groups, cascade delete
- [x] T018 Populate denormalized metric columns at trial write time — modify `SaveOptimizationAsync` in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs` to write `sharpe_ratio`, `sortino_ratio`, etc. from `PerformanceMetrics` alongside existing `fitness_score`
- [x] T019 [P] Create `OptimizationGroupContracts` in `src/AlgoTradeForge.WebApi/Contracts/OptimizationGroupContracts.cs` — DTOs: OptimizationGroupSubmissionResponse, OptimizationGroupSummaryResponse, OptimizationGroupDetailResponse, OptimizationGroupStatusResponse, GroupRunSummary
- [x] T020 [P] Create `ValidationGroupContracts` in `src/AlgoTradeForge.WebApi/Contracts/ValidationGroupContracts.cs` — DTOs: RunGroupValidationRequest (with maxTrialsToValidate field, default 100), ValidationGroupSubmissionResponse, ValidationGroupDetailResponse, ValidationGroupStatusResponse
- [x] T021 Add Serilog structured logging for group lifecycle events in `src/AlgoTradeForge.Application/Optimization/RunGroupOptimizationCommandHandler.cs` — log group created (with groupId, strategyName, dssCount), child run started, child run completed/failed (with runId, dssIndex, duration), group status transition (with groupId, oldStatus, newStatus)

**Checkpoint**: Foundation ready — data layer, records, repositories, DTOs, logging all in place

---

## Phase 3: User Story 1 — Per-DSS Independent Optimization (Priority: P1) 🎯 MVP

**Goal**: Each DSS runs as an independent optimization within a group. Results per DSS available immediately.

**Independent Test**: Launch optimization with 3 DSS, verify 3 independent runs created, results appear per-DSS as each completes.

### Tests for US1

- [ ] T022 [P] [US1] Test `RunGroupOptimizationCommandHandler` brute-force path in `tests/AlgoTradeForge.Application.Tests/Optimization/RunGroupOptimizationCommandHandlerTests.cs` — mock repository + helper, verify: group created, N child runs created, Channel consumers call ExecuteTrial with correct DssIndex, per-DSS progress tracked, group status updated on each child completion
- [ ] T023 [P] [US1] Test `RunGroupOptimizationCommandHandler` genetic path in `tests/AlgoTradeForge.Application.Tests/Optimization/RunGroupOptimizationCommandHandlerTests.cs` — verify: per-DSS GA populations, shared Channel for evaluation, independent evolution per DSS
- [ ] T024 [P] [US1] Test group dedup in `tests/AlgoTradeForge.Application.Tests/Optimization/RunGroupOptimizationCommandHandlerTests.cs` — verify submitting identical params while group is in-progress returns existing group

### Implementation for US1

- [x] T025 [US1] Create `RunGroupOptimizationCommand` in `src/AlgoTradeForge.Application/Optimization/RunGroupOptimizationCommand.cs` — single command with `OptimizationMethod` (BruteForce/Genetic), StrategyName, Axes, SubscriptionAxis (List<List<DataSubscriptionDto>>), BacktestSettings, MaxDegreeOfParallelism, MaxCombinations, MaxTrialsToKeep, trial filters, FitnessConfig, GeneticSettings (optional), InputJson
- [x] T026 [US1] Create `RunGroupOptimizationCommandHandler` — brute-force Channel orchestration in `src/AlgoTradeForge.Application/Optimization/RunGroupOptimizationCommandHandler.cs`: (1) compute group RunKey via BuildGroupKey, check dedup, (2) resolve subscriptions + load data caches per DSS, (3) resolve axes WITHOUT AppendSubscriptionAxis, (4) insert group + N child run placeholders, (5) create bounded Channel<(int DssIndex, ParameterCombination)>, (6) spawn maxParallelism LongRunning consumer tasks pulling from channel + calling helper.ExecuteTrial with DssIndex-specific data, (7) producer round-robin enqueues combos across DSS, (8) per-DSS BoundedTrialQueue + progress counters indexed by DssIndex, (9) on each DSS exhaustion: save per-run results + update group status via GroupStatusCalculator
- [x] T027 [US1] Add genetic mode to `RunGroupOptimizationCommandHandler` in `src/AlgoTradeForge.Application/Optimization/RunGroupOptimizationCommandHandler.cs` — per-DSS GA loops push evaluation work items into the shared Channel with ManualResetEventSlim for generation sync; evolution (selection, crossover, mutation) happens per-DSS between generations; consumer tasks are shared with brute-force path
- [x] T028 [US1] Modify `OptimizationSetupHelper` in `src/AlgoTradeForge.Application/Optimization/OptimizationSetupHelper.cs` — remove or bypass `AppendSubscriptionAxisAndFilter` when DSS is handled by group handler; keep method for backward compat but group handler calls axis resolution without subscription axis append
- [x] T029 [US1] Modify `POST /api/optimizations` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — when subscriptionAxis has >0 entries, dispatch to RunGroupOptimizationCommand; return OptimizationGroupSubmissionResponse with groupId + per-run details
- [x] T030 [US1] Modify `POST /api/optimizations/genetic` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — same group dispatch pattern for genetic mode
- [x] T031 [US1] Modify `POST /api/optimizations/evaluate` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — return per-run combination count (not multiplied by DSS count) + `dssCount` field
- [ ] T032 [P] [US1] Test optimization group endpoints in `tests/AlgoTradeForge.WebApi.Tests/OptimizationEndpointGroupTests.cs` — POST creates group, response contains groupId + runs array, evaluate returns dssCount

**Checkpoint**: Per-DSS optimization launches independently. Results per DSS are saved immediately. Group status tracked.

---

## Phase 4: User Story 2 — Optimization Group Tracking (Priority: P2)

**Goal**: Groups appear as expandable primary rows in the optimization list. Each DSS run visible as nested row.

**Independent Test**: View optimization list, verify groups are top-level expandable rows with nested DSS runs showing independent status.

### Tests for US2

- [x] T033 [P] [US2] Test optimization group management endpoints (list, detail, status, cancel, delete) in `tests/AlgoTradeForge.WebApi.Tests/OptimizationEndpointGroupTests.cs` — verify: GET /api/optimizations returns groups, GET groups/{groupId} returns detail with child runs, status returns per-run progress, cancel preserves completed runs, delete cascades

### Implementation for US2

- [x] T034 [US2] Add group list endpoint: modify `GET /api/optimizations` in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` to return OptimizationGroupSummaryResponse items (groups with status, totalRuns, completedRuns, failedRuns, subscriptions array)
- [x] T035 [US2] Add `GET /api/optimizations/groups/{groupId}` detail endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — returns group detail with nested child run summaries
- [x] T036 [US2] Add `GET /api/optimizations/groups/{groupId}/status` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — returns per-run progress (processed/total) from RunProgressCache
- [x] T037 [US2] Add `POST /api/optimizations/groups/{groupId}/cancel` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — cancel all in-progress runs via CancellationRegistry, preserve completed
- [x] T038 [US2] Add `DELETE /api/optimizations/groups/{groupId}` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — cascade delete group + all child runs + trials + failed trials
- [x] T039 [P] [US2] Add optimization group TypeScript types in `frontend/types/optimization.ts` — OptimizationGroup, OptimizationGroupSummary, OptimizationGroupDetail, OptimizationGroupStatus, GroupRunSummary
- [x] T040 [P] [US2] Add group hooks in `frontend/hooks/use-optimizations.ts` — useOptimizationGroups (list query), useOptimizationGroupDetail (by groupId), useOptimizationGroupStatus (polling), useCancelOptimizationGroup, useDeleteOptimizationGroup
- [x] T041 [US2] Create `optimization-groups-table.tsx` in `frontend/components/features/optimization/optimization-groups-table.tsx` — expandable group rows with: strategy, method, status, totalRuns, completedRuns, startedAt, subscriptions summary; expanded state shows nested DSS run rows with individual status, trial count, duration
- [x] T042 [US2] Modify optimization list page `frontend/app/[strategy]/optimization/page.tsx` to use optimization-groups-table instead of RunsTable; pass DashboardContent mode awareness

**Checkpoint**: Optimization list shows expandable group rows with nested DSS runs.

---

## Phase 5: User Story 3 — Enhanced Trial and Backtest Table (Priority: P3)

**Goal**: Trials table gains Params column, sortable metric columns, and clickable trial IDs that open a backtest launch side panel.

**Independent Test**: View any optimization's trials, verify Params column, sort by each metric, click trial ID to see pre-populated backtest panel.

### Tests for US3

- [x] T043 [P] [US3] Test sortBy and params field in trials endpoint in `tests/AlgoTradeForge.WebApi.Tests/OptimizationEndpointGroupTests.cs` — verify: GET /api/optimizations/{id}/trials returns params field, supports sortBy on each denormalized metric column

### Implementation for US3

- [x] T044 [US3] Add `params` field to trial response DTOs — modify backtest run mapping in `src/AlgoTradeForge.Infrastructure/Persistence/SqliteRunRepository.cs` to compute CSV params string from `parameters_json` (filter out DataSubscriptions key, format `Key:Value` pairs, ModuleSlot:VariantTypeKey for modules)
- [x] T045 [US3] Add `params` field to trial response contract in `src/AlgoTradeForge.WebApi/Contracts/OptimizationContracts.cs` (existing optimization trial response model)
- [x] T046 [US3] Modify `GET /api/optimizations/{id}/trials` in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` to support sortBy parameter using denormalized metric columns (sharpe_ratio, sortino_ratio, profit_factor, etc.) and include params field in response
- [x] T047 [US3] Add sortBy support to `GET /api/backtests` list endpoint in `src/AlgoTradeForge.WebApi/Endpoints/BacktestEndpoints.cs` — use same denormalized metric columns for backtest table sorting (FR-006)
- [x] T048 [US3] Modify `optimization-trials-table.tsx` in `frontend/components/features/report/optimization-trials-table.tsx` — add Params column as last column, make all metric column headers clickable for sort (asc/desc toggle), pass sortBy to useInfiniteOptimizationTrials hook
- [x] T049 [US3] Create `trial-backtest-panel.tsx` in `frontend/components/features/report/trial-backtest-panel.tsx` — on trial ID click: construct RunBacktestRequest from trial parameters + DSS, call openWithContent() from RunNewContext to open RunNewPanel pre-populated with the trial's data
- [x] T050 [US3] Wire clickable trial IDs in `optimization-trials-table.tsx` — each trial ID renders as a link/button that triggers trial-backtest-panel with the trial's parametersJson and DSS data

**Checkpoint**: Trial tables have Params column, sortable metrics, clickable trial IDs opening pre-populated backtest panel.

---

## Phase 6: User Story 4 — Cross-DSS Comparison Table (Priority: P4)

**Goal**: New tab within an optimization group showing all trials from all DSS runs, sortable across DSS boundaries.

**Independent Test**: View completed optimization group, navigate to cross-DSS tab, verify combined trials from all DSS, sort by Sharpe across all.

### Tests for US4

- [x] T051 [P] [US4] Test cross-DSS trials endpoint in `tests/AlgoTradeForge.WebApi.Tests/OptimizationEndpointGroupTests.cs` — verify: GET /api/optimizations/groups/{groupId}/trials returns trials from all child runs with dss field, supports sortBy, pagination works across runs

### Implementation for US4

- [x] T052 [US4] Add `GET /api/optimizations/groups/{groupId}/trials` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/OptimizationEndpoints.cs` — paginated cross-DSS trials with sortBy using denormalized columns, includes runId + dss per trial
- [x] T053 [P] [US4] Add cross-DSS trials hook in `frontend/hooks/use-optimizations.ts` — useOptimizationGroupTrials(groupId, { sortBy, limit, offset }) with infinite scroll
- [x] T054 [US4] Create `cross-dss-trials-table.tsx` in `frontend/components/features/report/cross-dss-trials-table.tsx` — combined table with DSS column, all metric columns sortable, Params column, clickable trial IDs (reuse trial-backtest-panel from US3), initial grouping by DSS with sort breaking grouping
- [x] T055 [US4] Create `optimization-group-page.tsx` in `frontend/components/features/report/optimization-group-page.tsx` — tabbed layout: "Per-DSS Runs" tab (child run list with drill-down) + "Cross-DSS" tab (cross-dss-trials-table), group header with status/metadata
- [x] T056 [US4] Create group report route `frontend/app/report/optimization-group/[groupId]/page.tsx` — server component loading optimization-group-page

**Checkpoint**: Cross-DSS comparison table works, sortable across all DSS.

---

## Phase 7: User Story 5 — DSS Builder in Optimization and Backtest Creation (Priority: P5)

**Goal**: Visual collapsible DSS builder above the JSON editor. Backtest form supports multi-DSS.

**Independent Test**: Open +New Optimization form, expand DSS builder, add 3 rows, verify subscriptionAxis populates in JSON. Open +New Backtest, select 3 DSS, verify 3 backtests launched.

### Implementation for US5

- [x] T057 [P] [US5] Create `dss-builder.tsx` in `frontend/components/features/dashboard/dss-builder.tsx` — collapsible section with table: each row has AssetName (text input), Exchange (text input), TimeFrame (text input), remove button. Add-row button. On change: serializes rows into `subscriptionAxis` format (List<List<DataSubscriptionDto>>), calls onChange callback
- [x] T058 [US5] Integrate DSS builder into `run-new-panel.tsx` in `frontend/components/features/dashboard/run-new-panel.tsx` — render DssBuilder above CodeMirror editor (collapsible, pre-collapsed). On builder change: parse current editor JSON, update subscriptionAxis field, set editor content. On editor change: sync builder state from subscriptionAxis field (bidirectional sync)
- [x] T059 [US5] Add multi-DSS backtest support — when mode is "backtest" and subscriptionAxis has >1 entry, modify submit handler in `run-new-panel.tsx` to launch N separate backtest requests (one per DSS) via api-client.runBacktest(), showing N submission confirmations

**Checkpoint**: DSS builder populates JSON, multi-DSS backtest launches N separate runs.

---

## Phase 8: User Story 6 — Per-DSS Validation with Group Reference (Priority: P6)

**Goal**: Validation launchable per optimization group. Cross-DSS validation tab.

**Independent Test**: Complete an optimization group, launch validation from it, verify per-DSS validation runs with cross-DSS comparison.

### Tests for US6

- [x] T060 [P] [US6] Test `RunGroupValidationCommandHandler` in `tests/AlgoTradeForge.Application.Tests/Validation/RunGroupValidationCommandHandlerTests.cs` — verify: validation group created, per-DSS validation runs reference source optimization runs, MaxTrialsToValidate cap applied, group status updated

### Implementation for US6

- [x] T061 [US6] Create `RunGroupValidationCommand` in `src/AlgoTradeForge.Application/Validation/RunGroupValidationCommand.cs` — fields: OptimizationGroupId, ThresholdProfileName, MaxTrialsToValidate (default 100)
- [x] T062 [US6] Create `RunGroupValidationCommandHandler` in `src/AlgoTradeForge.Application/Validation/RunGroupValidationCommandHandler.cs` — (1) load optimization group + child runs, (2) validate all or some child runs are Completed, (3) insert validation group placeholder, (4) for each completed DSS run: select top N trials by fitness (capped at MaxTrialsToValidate), create validation run referencing both validation_group_id and optimization_run_id, (5) launch per-DSS validation pipelines (can reuse existing RunValidationCommandHandler internally or call pipeline directly), (6) update validation group status on each child completion
- [x] T063 [US6] Modify `POST /api/validations` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/ValidationEndpoints.cs` — accept optimizationGroupId + thresholdProfileName + maxTrialsToValidate, dispatch to RunGroupValidationCommand, return ValidationGroupSubmissionResponse
- [x] T064 [US6] Modify `GET /api/validations` endpoint in `src/AlgoTradeForge.WebApi/Endpoints/ValidationEndpoints.cs` to return validation groups (ValidationGroupSummaryResponse items)
- [x] T065 [US6] Add validation group sub-routes in `src/AlgoTradeForge.WebApi/Endpoints/ValidationEndpoints.cs` — GET groups/{groupId}, GET groups/{groupId}/status, GET groups/{groupId}/trials, POST groups/{groupId}/cancel, DELETE groups/{groupId}
- [x] T066 [P] [US6] Test validation group endpoints in `tests/AlgoTradeForge.WebApi.Tests/ValidationEndpointGroupTests.cs`
- [x] T067 [P] [US6] Add validation group TypeScript types in `frontend/types/validation.ts` — ValidationGroup, ValidationGroupSummary, ValidationGroupDetail, RunGroupValidationRequest (with maxTrialsToValidate)
- [x] T068 [P] [US6] Add validation group hooks in `frontend/hooks/use-validations.ts` — useValidationGroups, useValidationGroupDetail, useValidationGroupStatus, useRunGroupValidation, useCancelValidationGroup, useDeleteValidationGroup
- [x] T069 [US6] Create `validation-group-page.tsx` in `frontend/components/features/report/validation-group-page.tsx` — tabbed layout: "Per-DSS Runs" tab (child validation list with verdict badges, link to source optimization group) + "Cross-DSS" tab (cross-DSS validation trials table with verdict/score columns)
- [x] T070 [US6] Create validation group report route `frontend/app/report/validation-group/[groupId]/page.tsx`
- [x] T071 [US6] Modify validation launch in `frontend/components/features/report/optimization-group-page.tsx` — "Run Validation" button on group page opens dialog dispatching RunGroupValidationRequest with optimizationGroupId

**Checkpoint**: Validation works per-DSS group with cross-DSS comparison.

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Final integration, cleanup, end-to-end validation

- [x] T072 [P] Wire navigation: optimization group list rows link to `/report/optimization-group/{groupId}`, validation group list links to `/report/validation-group/{groupId}` across `frontend/components/`
- [x] T073 [P] Add RunProgress polling for group status — integrate useOptimizationGroupStatus into optimization-group-page.tsx with 2-second polling while status is InProgress
- [x] T074 [P] Add re-run support for groups — "Re-run" button on optimization-group-page reads inputJson, stores in sessionStorage, navigates to optimization page (same pattern as existing re-run in RunNewPanel)
- [x] T075 Benchmark cross-DSS trial sorting with 10K+ rows per DSS run in `tests/AlgoTradeForge.Infrastructure.Tests/Persistence/SqliteRunRepository_GroupTests.cs` — verify sort completes in <1 second on SQLite (SC-003)
- [x] T076 Run quickstart.md validation — launch backend, launch frontend, execute API quick test from quickstart.md, verify end-to-end flow

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — T001–T006 are sequential schema changes in one file
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Foundational — core backend change, MVP
- **US2 (Phase 4)**: Depends on US1 (needs groups to exist for listing/display)
- **US3 (Phase 5)**: Depends on Foundational only (denormalized metrics from T018). Can run in parallel with US1/US2 for frontend table work; backend sortBy depends on T018
- **US4 (Phase 6)**: Depends on US1 (needs groups) + reuses US3 components (trial-backtest-panel)
- **US5 (Phase 7)**: Frontend work independent of other stories; backend submission depends on US1 endpoint changes
- **US6 (Phase 8)**: Depends on US1 (needs optimization groups to validate from)
- **Polish (Phase 9)**: Depends on all stories being complete

### User Story Dependencies

- **US1 (P1)**: After Foundational — no other story dependencies. **MVP target.**
- **US2 (P2)**: After US1 — needs group data to display
- **US3 (P3)**: After Foundational — mostly independent; frontend work parallelizable with US1/US2
- **US4 (P4)**: After US1 + US3 — needs groups + trial table components
- **US5 (P5)**: After Foundational — frontend mostly independent; submission requires US1 endpoint
- **US6 (P6)**: After US1 — needs optimization groups to validate

### Within Each User Story

- Tests written first, verify they fail
- Records/models before services
- Services/handlers before endpoints
- Backend before frontend
- Core implementation before integration

### Parallel Opportunities

- T007, T008, T009, T010, T019, T020 (Phase 2 records/DTOs) — all different files
- T016, T017 (Phase 2 repo tests) — different test files
- T022, T023, T024 (US1 tests) — same test class but can be written together
- T039, T040 (US2 frontend types/hooks) — different files
- T053, T057 (US4 hook + US5 builder) — different files, different stories
- T060, T066, T067, T068 (US6 tests/types/hooks) — different files
- T072, T073, T074 (Polish) — different components

---

## Parallel Example: User Story 1

```
# After Foundational phase completes, launch US1 tests in parallel:
Task T022: Test RunGroupOptimizationCommandHandler brute-force
Task T023: Test RunGroupOptimizationCommandHandler genetic
Task T024: Test group dedup

# Then implement command:
Task T025: Create RunGroupOptimizationCommand

# Then handler (split into brute-force + genetic):
Task T026: RunGroupOptimizationCommandHandler brute-force Channel orchestration (depends on T025)
Task T027: Add genetic mode to handler (depends on T026)

# In parallel with handler, modify setup helper:
Task T028: Modify OptimizationSetupHelper (independent file)

# Then wire endpoints (depends on T026):
Task T029: Modify POST /api/optimizations
Task T030: Modify POST /api/optimizations/genetic
Task T031: Modify POST /api/optimizations/evaluate (independent)

# Endpoint test in parallel:
Task T032: Test optimization group endpoints
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (schema changes)
2. Complete Phase 2: Foundational (records, repos, DTOs, logging)
3. Complete Phase 3: User Story 1 (Channel orchestrator + endpoints)
4. **STOP and VALIDATE**: Launch 3-DSS optimization via API, verify 3 independent runs complete with per-DSS results
5. Deploy/demo if ready — backend fully functional for per-DSS optimization

### Incremental Delivery

1. Setup + Foundational → Data layer ready
2. US1 → Per-DSS optimization works (API only) → **MVP**
3. US2 → Group list visible in frontend → Demo-ready
4. US3 → Trial tables enhanced (Params, sorting, clickable IDs) → Better UX
5. US4 → Cross-DSS comparison → Full analysis capability
6. US5 → DSS builder in forms → Complete input UX
7. US6 → Validation per group → Full pipeline
8. Polish → Navigation, polling, re-run, benchmarks → Production-ready

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story independently completable and testable
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- FR-014: All existing optimization/validation data is dropped — no migration needed
