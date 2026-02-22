# Tasks: Trading Frontend

**Input**: Design documents from `/specs/008-trading-frontend/`
**Prerequisites**: plan.md, spec.md, data-model.md, contracts/api-endpoints.md, research.md, quickstart.md

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Next.js project initialization, dependencies, and configuration

- [X] T001 Initialize Next.js 16 project with TypeScript strict mode, install dependencies (react, next, tailwindcss, @tailwindcss/postcss, @tanstack/react-query, zustand, lightweight-charts, codemirror, @codemirror/lang-json) in `frontend/`
- [X] T002 [P] Configure Tailwind CSS 4 dark theme with CSS-first `@theme` design tokens (bg-surface, bg-panel, text-primary, text-secondary, accent colors, chart palette) in `frontend/app/globals.css` (no `tailwind.config.ts` — Tailwind v4 uses `@import "tailwindcss"` + `@theme` blocks)
- [X] T003 [P] Configure Vitest with React Testing Library and path aliases in `frontend/vitest.config.ts`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core types, service layer, UI primitives, and app shell that ALL user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete

### Types

- [X] T004 [P] Define API response and request types (BacktestRun, OptimizationRun, PagedResponse, EquityPoint, EventsData, CandleData, IndicatorSeries, TradeData, RunBacktestRequest, RunOptimizationRequest, StartDebugSessionRequest, DebugSession, DebugSessionStatus, DebugSnapshot, DebugCommand, ServerMessage) in `frontend/types/api.ts`
- [X] T005 [P] Define backtest event types (EventType union, BacktestEvent envelope, BarEventData, IndicatorEventData, SignalEventData, RiskEventData, OrderPlaceEventData, OrderFillEventData, OrderCancelEventData, OrderRejectEventData, PositionEventData, RunStartEventData, RunEndEventData, ErrorEventData, WarningEventData) in `frontend/lib/events/types.ts` with re-exports from `frontend/types/events.ts`
- [X] T006 [P] Define chart-specific data types (chart configuration, series options, marker types) in `frontend/types/chart.ts`

### Service Layer

- [X] T007 Implement fetch-based API client with all endpoint methods (strategies list, backtests CRUD + equity + events, optimizations CRUD, debug sessions CRUD) in `frontend/lib/services/api-client.ts`
- [X] T008 Create service layer index that exports active client based on NEXT_PUBLIC_MOCK_MODE env var in `frontend/lib/services/index.ts`

### Utilities

- [X] T009 [P] Implement camelCase-to-Title-Case formatter, number/currency/percentage formatters, and duration formatter in `frontend/lib/utils/format.ts`, and structured logging utility (debug/info/warn/error levels with component context tag) in `frontend/lib/utils/logger.ts`
- [X] T010 [P] Implement TradingView chart helpers (create chart with dark theme defaults, add series, create markers, cleanup) in `frontend/lib/utils/chart-utils.ts`
- [X] T011 [P] Implement JSONL event parser with message discriminator (type field → snapshot/error/ack, _t field → backtest event) in `frontend/lib/events/parser.ts`

### UI Primitives

- [X] T012 [P] Create Button component (primary, secondary, ghost, danger variants; disabled state; loading spinner) in `frontend/components/ui/button.tsx`
- [X] T013 [P] Create data Table component with dynamic columns, row click handler, and horizontal scroll support in `frontend/components/ui/table.tsx`
- [X] T014 [P] Create Skeleton loader component (line, rect, chart variants) in `frontend/components/ui/skeleton.tsx`
- [X] T015 [P] Create Toast notification component and provider (success, error, info variants with auto-dismiss) in `frontend/components/ui/toast.tsx`
- [X] T016 [P] Create Tabs component (controlled, with tab panels) in `frontend/components/ui/tabs.tsx`
- [X] T017 [P] Create SlideOver panel component (right-side slide, overlay, close button, scroll body) in `frontend/components/ui/slide-over.tsx`
- [X] T018 [P] Create Pagination component with offset-based navigation using hasMore flag in `frontend/components/ui/pagination.tsx`

### App Shell

- [X] T019 Create root layout with dark theme body, header (app name "AlgoTradeForge", nav links: Dashboard, Debug, Docs placeholder, Settings placeholder), footer (version), TanStack QueryClientProvider in `frontend/app/layout.tsx`
- [X] T020 [P] Create root page with redirect to /dashboard in `frontend/app/page.tsx`
- [X] T021 [P] Create ChartSkeleton placeholder component (pulsing rect matching chart dimensions) in `frontend/components/features/charts/chart-skeleton.tsx`

**Checkpoint**: Foundation ready — user story implementation can now begin

---

## Phase 3: User Story 1 — Step Through Strategy Execution in Debug Mode (Priority: P1) MVP

**Goal**: Build the interactive debug screen where traders step through strategy execution via WebSocket, seeing candles, indicators, and trades build progressively on a TradingView chart.

**Independent Test**: Start a debug session (via JSON editor + Start button), step forward one bar, verify a bar event arrives via WebSocket and the chart adds one candle with updated indicators. Step to next trade and verify order lifecycle events render as trade markers.

### Implementation for User Story 1

- [X] T022 [P] [US1] Create Zustand debug session store (session state enum: idle/configuring/connecting/active/stopped, accumulated candles array, indicators map, trades array, latest snapshot, error message) in `frontend/lib/stores/debug-store.ts`
- [X] T023 [US1] Create useDebugWebSocket hook (WebSocket lifecycle tied to sessionId, JSONL message parsing via event parser, command sending methods, snapshot/error handling, Zustand store integration, cleanup on unmount, connection drop detection with error notification and session cleanup; in mock mode: accept injected event source that replays mock fixtures from T036 instead of opening a real WebSocket) in `frontend/hooks/use-debug-websocket.ts`
- [X] T024 [P] [US1] Create CandlestickChart client component with next/dynamic ssr:false wrapper, incremental update mode using ISeriesApi.update() for real-time bar additions, indicator line series with legend, order placement markers (from ord.place), trade fill markers (from ord.fill), position markers (from pos), requestAnimationFrame batching, chart.remove() cleanup in `frontend/components/features/charts/candlestick-chart.tsx` (TP/SL horizontal lines deferred to future iteration for debug mode)
- [X] T025 [P] [US1] Create SessionConfigEditor with CodeMirror 6 JSON editor (dark theme, syntax validation, pre-filled default StartDebugSessionRequest template) and "Start" submit button in `frontend/components/features/debug/session-config-editor.tsx`
- [X] T026 [P] [US1] Create DebugToolbar with buttons: Next (next), To Next Bar (next_bar), To Next Trade (next_trade), To Next Signal (next_signal), Run To Timestamp input+button (run_to_timestamp), Run To Sequence input+button (run_to_sequence), Play (continue), Pause (pause), Stop (DELETE session), Set Export toggle (set_export with mutations boolean) in `frontend/components/features/debug/debug-toolbar.tsx`
- [X] T027 [P] [US1] Create DebugMetrics panel displaying snapshot data (sessionActive, sequenceNumber, timestampMs formatted as datetime, portfolioEquity, fillsThisBar, subscriptionIndex) in `frontend/components/features/debug/debug-metrics.tsx`
- [X] T028 [US1] Create debug page with full session lifecycle: idle state shows SessionConfigEditor → on Start: POST /api/debug-sessions then open WebSocket → active state shows toolbar + candlestick chart + metrics panel → on Stop: close WebSocket + DELETE session → confirmation prompt on navigate-away during active session in `frontend/app/debug/page.tsx`
- [X] T029 [P] [US1] Create debug loading skeleton in `frontend/app/debug/loading.tsx`
- [X] T030 [P] [US1] Create debug error boundary with retry option in `frontend/app/debug/error.tsx`

**Checkpoint**: Debug screen (MVP) is fully functional — users can configure, start, step through, and stop debug sessions with real-time chart updates

---

## Phase 4: User Story 5 — Frontend Development Without Backend (Priority: P2)

**Goal**: Enable full frontend development and demonstration using realistic mock data without a running backend, including simulated WebSocket event replay for the debug screen.

**Independent Test**: Enable NEXT_PUBLIC_MOCK_MODE=true, navigate through all screens (dashboard, reports, debug), verify realistic data appears everywhere with no network errors.

### Implementation for User Story 5

- [X] T031 [P] [US5] Create mock strategies fixture (3-5 strategy names) in `frontend/lib/services/mock-data/strategies.json`
- [X] T032 [P] [US5] Create mock backtests fixture (paged response with 10+ runs, varied strategies/assets/metrics) in `frontend/lib/services/mock-data/backtests.json`
- [X] T033 [P] [US5] Create mock optimizations fixture (2-3 optimization runs with trials arrays) in `frontend/lib/services/mock-data/optimizations.json`
- [X] T034 [P] [US5] Create mock equity curve fixture (50+ timestampMs/value data points) in `frontend/lib/services/mock-data/equity.json`
- [X] T035 [P] [US5] Create mock events data fixture (candles, indicators with measure types, trades with TP/SL) in `frontend/lib/services/mock-data/events.json`
- [X] T036 [P] [US5] Create mock debug event replay sequence (30+ JSONL events including bar, ind, sig, ord.fill, pos events interleaved with snapshot messages) in `frontend/lib/services/mock-data/debug-events.jsonl`
- [X] T037 [US5] Implement mock client matching API client interface: fixture loading for all list/detail/equity/events endpoints, paging simulation, filter matching, simulated WebSocket replay with timed event emission from debug-events.jsonl, mock POST responses for run/session creation in `frontend/lib/services/mock-client.ts`

**Checkpoint**: All screens functional with NEXT_PUBLIC_MOCK_MODE=true — no backend required for development

---

## Phase 5: User Story 2 — View Detailed Run Report (Priority: P2)

**Goal**: Build report screens for backtests (equity chart, metrics, params, conditional candlestick chart with indicators and trade markers) and optimizations (summary + trials table with drill-down to individual trial reports).

**Independent Test**: Navigate to a backtest report URL, verify equity chart, metrics panel, params panel, and candlestick chart (with indicator overlays and trade markers) all render correctly. Navigate to an optimization report, verify summary and trials table render with clickable trial rows.

### Implementation for User Story 2

- [X] T038 [P] [US2] Create TanStack Query hooks for backtest detail, equity curve, and events data (useBacktestDetail, useBacktestEquity, useBacktestEvents) in `frontend/hooks/use-backtests.ts`
- [X] T039 [P] [US2] Create TanStack Query hook for optimization detail with trials (useOptimizationDetail) in `frontend/hooks/use-optimizations.ts`
- [X] T040 [US2] Create EquityChart client component with next/dynamic ssr:false wrapper, TradingView line series, crosshair tooltips, bulk setData() loading, fitContent() on load, chart.remove() cleanup in `frontend/components/features/charts/equity-chart.tsx`
- [X] T041 [P] [US2] Create MetricsPanel component (iterates metrics dictionary, formats camelCase keys to Title Case, formats numeric values with appropriate precision) in `frontend/components/features/report/metrics-panel.tsx`
- [X] T042 [P] [US2] Create ParamsPanel component (iterates parameters dictionary, displays key-value pairs with formatted labels) in `frontend/components/features/report/params-panel.tsx`
- [X] T043 [US2] Add bulk data loading mode to CandlestickChart (setData() for candles + indicators + trades + TP/SL horizontal price lines spanning each trade's duration, fitContent() after load, used by report screen vs incremental mode used by debug) in `frontend/components/features/charts/candlestick-chart.tsx`
- [X] T044 [US2] Create backtest report page: fetch backtest detail + equity + conditional events, render EquityChart, MetricsPanel, ParamsPanel, conditionally render CandlestickChart when hasCandleData is true in `frontend/app/report/backtest/[id]/page.tsx`
- [X] T045 [P] [US2] Create backtest report loading skeleton in `frontend/app/report/backtest/[id]/loading.tsx`
- [X] T046 [P] [US2] Create backtest report error boundary in `frontend/app/report/backtest/[id]/error.tsx`
- [X] T047 [P] [US2] Create OptimizationTrialsTable component (columns: trial parameters, key metrics; row click navigates to /report/backtest/[trialId]) in `frontend/components/features/report/optimization-trials-table.tsx`
- [X] T048 [US2] Create optimization report page: fetch optimization detail, render summary (totalCombinations, durationMs, sortBy), render OptimizationTrialsTable with trials array in `frontend/app/report/optimization/[id]/page.tsx`
- [X] T049 [P] [US2] Create optimization report loading skeleton and error boundary in `frontend/app/report/optimization/[id]/loading.tsx` and `frontend/app/report/optimization/[id]/error.tsx`

**Checkpoint**: Both backtest and optimization report screens are fully functional with all chart components, metrics, and navigation

---

## Phase 6: User Story 3 — Browse and Filter Historical Runs (Priority: P3)

**Goal**: Build the dashboard with strategy selector sidebar, mode tabs (Backtest/Optimization), filterable paged runs tables, and row-click navigation to report screens.

**Independent Test**: Load the dashboard, select a strategy, verify the runs table populates. Apply filters (asset, exchange, timeframe, date range), verify the table re-fetches. Click a row to navigate to the report screen. Switch to Optimization tab, verify optimization runs appear. Page through results.

### Implementation for User Story 3

- [X] T050 [P] [US3] Create useStrategies TanStack Query hook in `frontend/hooks/use-strategies.ts`
- [X] T051 [P] [US3] Add useBacktestList TanStack Query hook (with strategyName, assetName, exchange, timeFrame, from, to, standaloneOnly, limit, offset query params) to `frontend/hooks/use-backtests.ts`
- [X] T052 [P] [US3] Add useOptimizationList TanStack Query hook (with strategyName, assetName, exchange, timeFrame, from, to, limit, offset query params) to `frontend/hooks/use-optimizations.ts`
- [X] T053 [P] [US3] Create StrategySelector sidebar component (fetches strategy list, renders clickable list items, highlights selected) in `frontend/components/features/dashboard/strategy-selector.tsx`
- [X] T054 [P] [US3] Create RunFilters component (inputs for assetName, exchange, timeFrame, date range from/to, applies filters as query params) in `frontend/components/features/dashboard/run-filters.tsx`
- [X] T055 [US3] Create RunsTable component (dynamic columns from metrics dictionary: StrategyVersion, RunId, Asset, Exchange, TF, Sortino, Sharpe, Profit Factor + additional metrics; row click navigates to /report/backtest/[id] or /report/optimization/[id]; empty state message) in `frontend/components/features/dashboard/runs-table.tsx`
- [X] T056 [US3] Create dashboard layout with sidebar containing StrategySelector in `frontend/app/dashboard/layout.tsx`
- [X] T057 [US3] Create dashboard page with mode Tabs (Backtest/Optimization), RunFilters, RunsTable, Pagination, integrated with TanStack Query hooks and strategy selection state in `frontend/app/dashboard/page.tsx`
- [X] T058 [P] [US3] Create dashboard loading skeleton in `frontend/app/dashboard/loading.tsx`
- [X] T059 [P] [US3] Create dashboard error boundary in `frontend/app/dashboard/error.tsx`

**Checkpoint**: Dashboard is fully functional — users can browse, filter, page through, and navigate to any run's report

---

## Phase 7: User Story 4 — Launch a New Run (Priority: P4)

**Goal**: Add a "+ Run New" button to the dashboard that opens a slide-over panel with a mode-aware JSON editor for submitting new backtests or optimizations.

**Independent Test**: Click "+ Run New", edit the JSON, submit, verify a loading state appears followed by the table refreshing with the new run entry. Verify error toast on failed submission.

### Implementation for User Story 4

- [X] T060 [US4] Create RunNewPanel component: slide-over with CodeMirror JSON editor, mode-aware defaults (RunBacktestRequest template for Backtest tab, RunOptimizationRequest template for Optimization tab), JSON validation, "Run" submit button, loading state, success closes panel and triggers table refetch, error shows toast in `frontend/components/features/dashboard/run-new-panel.tsx`
- [X] T061 [US4] Integrate RunNewPanel into dashboard page: add "+ Run New" button, pass current mode tab, wire panel open/close state, trigger TanStack Query invalidation on successful submission in `frontend/app/dashboard/page.tsx`

**Checkpoint**: Users can launch new backtests and optimizations directly from the dashboard

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Edge case handling, performance optimizations, and responsive design improvements

- [X] T062 Add navigation-away confirmation prompt for active debug sessions (beforeunload event + Next.js route change interception) in `frontend/app/debug/page.tsx`
- [X] T063 [P] Add responsive layout: collapsible sidebar to icons on screens <1280px, horizontal table scrolling, chart container resize handling in `frontend/app/dashboard/layout.tsx` and `frontend/components/ui/table.tsx`
- [X] T064 [P] Add event buffering with requestAnimationFrame batching for debug auto-play mode (batch rapid events to prevent layout thrashing) in `frontend/hooks/use-debug-websocket.ts`
- [X] T065 Run quickstart.md validation: verify dev server starts, all routes render, mock mode serves data on all screens, no TypeScript errors, and basic timing check that filter updates complete in <1s, report pages render in <2s, and debug step updates appear in <1s (SC-003, SC-004, SC-006)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 Debug (Phase 3)**: Depends on Foundational (Phase 2) — No dependencies on other stories
- **US5 Mock Mode (Phase 4)**: Depends on Foundational (Phase 2) — Independent of other stories, but enables development of all screens
- **US2 Report (Phase 5)**: Depends on Foundational (Phase 2) — Can share CandlestickChart from US1 (T024/T043) but independently testable
- **US3 Dashboard (Phase 6)**: Depends on Foundational (Phase 2) — Links to US2 report pages for navigation but independently testable
- **US4 Run New (Phase 7)**: Depends on US3 Dashboard (Phase 6) — Integrates into the dashboard page
- **Polish (Phase 8)**: Depends on all desired user stories being complete

### User Story Dependencies

- **US1 (P1 Debug)**: Can start after Phase 2 — No dependencies on other stories
- **US5 (P2 Mock Mode)**: Can start after Phase 2 — Independent; enables parallel frontend development
- **US2 (P2 Report)**: Can start after Phase 2 — Extends CandlestickChart from US1 (T043 adds bulk mode to T024) but can be built independently
- **US3 (P3 Dashboard)**: Can start after Phase 2 — Row navigation targets US2 report routes, but table and filters work independently
- **US4 (P4 Run New)**: Depends on US3 — Integrates into the dashboard page as a slide-over panel

### Within Each User Story

- Stores/hooks before components that consume them
- Leaf components (panels, toolbars) before page components that compose them
- Page component last (assembles all components for the story)
- Loading/error boundaries can be created in parallel with page component

### Cross-Story File Dependencies

- `frontend/hooks/use-backtests.ts`: Created in US2 (T038), extended in US3 (T051)
- `frontend/hooks/use-optimizations.ts`: Created in US2 (T039), extended in US3 (T052)
- `frontend/components/features/charts/candlestick-chart.tsx`: Created in US1 (T024), extended in US2 (T043)
- `frontend/app/dashboard/page.tsx`: Created in US3 (T057), extended in US4 (T061)

**Sequencing constraint**: If US2 and US3 are developed in parallel by different developers, the shared hook files (`use-backtests.ts`, `use-optimizations.ts`) will cause merge conflicts. To avoid this, either: (a) one developer creates the hook files first with both sets of exports, or (b) US2 hooks are completed and merged before US3 hook extensions begin. The candlestick-chart.tsx has the same constraint between US1 and US2.

### Parallel Opportunities

- All Setup tasks T002-T003 can run in parallel (after T001)
- All Foundational type tasks T004-T006 can run in parallel
- All Foundational UI tasks T012-T018 can run in parallel
- T019-T021 can run in parallel (after T007-T008)
- US1: T022, T024, T025, T026, T027 can run in parallel; T023 depends on T022; T028 depends on T023-T027
- US5: T031-T036 all run in parallel; T037 depends on all fixtures
- US2: T038-T039 in parallel, T041-T042 in parallel, T045-T046-T047-T049 in parallel
- US3: T050-T054 all in parallel; T055 depends on T013 (Table); T057 depends on T053-T055
- Once Phase 2 completes: US1, US5, US2, US3 can start in parallel (if staffed)

---

## Parallel Example: User Story 1

```bash
# After Phase 2 completes, launch independent US1 tasks in parallel:
Task: "T022 [P] [US1] Create Zustand debug session store in frontend/lib/stores/debug-store.ts"
Task: "T024 [P] [US1] Create CandlestickChart in frontend/components/features/charts/candlestick-chart.tsx"
Task: "T025 [P] [US1] Create SessionConfigEditor in frontend/components/features/debug/session-config-editor.tsx"
Task: "T026 [P] [US1] Create DebugToolbar in frontend/components/features/debug/debug-toolbar.tsx"
Task: "T027 [P] [US1] Create DebugMetrics in frontend/components/features/debug/debug-metrics.tsx"

# Then sequentially (depends on T022):
Task: "T023 [US1] Create useDebugWebSocket hook in frontend/hooks/use-debug-websocket.ts"

# Finally (depends on all above):
Task: "T028 [US1] Create debug page in frontend/app/debug/page.tsx"

# Loading/error can run in parallel with T028:
Task: "T029 [P] [US1] Create debug loading skeleton in frontend/app/debug/loading.tsx"
Task: "T030 [P] [US1] Create debug error boundary in frontend/app/debug/error.tsx"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (Debug Screen)
4. **STOP and VALIDATE**: Test debug session lifecycle end-to-end
5. Optionally add Phase 4 (Mock Mode) to enable demo without backend

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add US1 (Debug) → Test independently → MVP!
3. Add US5 (Mock Mode) → Full development without backend
4. Add US2 (Report) → Analytical views for completed runs
5. Add US3 (Dashboard) → Central navigation hub with filtering
6. Add US4 (Run New) → Launch runs from the UI
7. Polish → Responsive layout, performance, edge cases
8. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1 (Debug — MVP)
   - Developer B: User Story 5 (Mock Mode — enables all development)
3. After US1 complete:
   - Developer A: User Story 2 (Report — extends CandlestickChart from US1)
   - Developer B: User Story 3 (Dashboard)
4. After US3 complete:
   - Any developer: User Story 4 (Run New — integrates into Dashboard)
5. Polish phase after all stories complete

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks in same phase
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- No test tasks generated (not explicitly requested in spec)
- Backend prerequisite: GET /api/backtests/{id}/events endpoint needed for US2 candlestick chart (T043/T044)
- Total tasks: 65 (Setup: 3, Foundational: 18, US1: 9, US5: 7, US2: 12, US3: 10, US4: 2, Polish: 4)
