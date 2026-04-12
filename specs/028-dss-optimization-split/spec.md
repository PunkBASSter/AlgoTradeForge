# Feature Specification: Per-DSS Optimization Split

**Feature Branch**: `028-dss-optimization-split`  
**Created**: 2026-04-12  
**Status**: Draft  
**Input**: User description: "Split optimization by data subscription sets so each DSS gets independent optimization with immediate results, cross-DSS comparison, enhanced trial tables, and DSS-based validation grouping."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Per-DSS Independent Optimization (Priority: P1)

A trader launches an optimization for a strategy that trades across multiple assets (e.g., BTC, ETH, SOL). Instead of waiting for ALL asset-parameter combinations to finish before seeing any results, each data subscription set (DSS) runs its own independent optimization. The trader can view BTC optimization results immediately after BTC finishes, even while ETH and SOL are still running. In genetic mode, selection and crossover happen within each DSS population independently, preserving the strongest survivors per asset rather than having cross-asset genetic pressure eliminate entire assets.

**Why this priority**: This is the core architectural change that drives the entire feature. Without per-DSS isolation, all other stories (grouping, cross-DSS tables, validation) have no foundation. It also directly solves the two primary pain points: delayed result visibility and genetic survivor bias eliminating cross-asset coverage.

**Independent Test**: Can be fully tested by launching an optimization on a strategy with 3+ data subscriptions and verifying that each DSS produces results independently, with results accessible before other DSS optimizations complete.

**Acceptance Scenarios**:

1. **Given** a strategy with 3 data subscriptions (BTC, ETH, SOL) and a parameter grid of 50 combinations, **When** the user launches a brute-force optimization, **Then** the system creates 3 independent optimization runs (one per DSS), each processing 50 parameter combinations, and results for each DSS appear as soon as that DSS completes regardless of others.
2. **Given** a strategy with 2 data subscriptions and genetic mode selected, **When** the user launches a genetic optimization, **Then** each DSS gets its own independent genetic population, performing selection/crossover within its own candidate pool, and the top fitness results are ranked per DSS.
3. **Given** an optimization group with 3 DSS runs in progress, **When** one DSS run fails or is cancelled, **Then** the remaining DSS runs continue unaffected, and the failed run shows its error independently.
4. **Given** a multi-asset strategy that uses multiple subscriptions per asset (e.g., BTC spot + BTC perpetual), **When** the user launches optimization, **Then** each subscription set (not individual subscription) gets its own optimization run.

---

### User Story 2 - Optimization Group Tracking (Priority: P2)

When a trader launches a per-DSS optimization, all the individual DSS runs that were launched together are visually and logically grouped under a single "optimization group." The optimization list shows these groups, and individual DSS runs within a group are visually connected. Each group has a single group ID that ties the runs together.

**Why this priority**: Grouping is essential for organizing the per-DSS runs from Story 1. Without it, the optimization list becomes a flat, disconnected set of individual runs with no way to tell which ones were launched together.

**Independent Test**: Can be tested by launching a grouped optimization and verifying the group appears as a single logical entity in the optimization list, with individual DSS runs nested within.

**Acceptance Scenarios**:

1. **Given** a user launches an optimization for a strategy across 4 DSS, **When** the optimization list loads, **Then** the group appears as a single primary row showing summary information (strategy, total DSS count, completed count, overall status).
2. **Given** an optimization group row in the list, **When** the user expands it, **Then** the 4 individual DSS runs are revealed as nested rows, each showing its own status (InProgress, Completed, Failed, Cancelled), trial count, and duration independently.
3. **Given** multiple optimization groups exist for the same strategy, **When** the user views the optimization list, **Then** each group is a distinct expandable/collapsible row at the top level.

---

### User Story 3 - Enhanced Trial and Backtest Table (Priority: P3)

For each optimization run (per DSS) and for backtests, the trial results table gains a new "Params" column that displays a CSV-formatted string of key:value pairs for each trial's parameter combination. All metric columns become sortable. Each trial ID is clickable and opens a backtest launch side panel with the trial's parameters pre-populated in the JSON editor, ready to start.

**Why this priority**: The Params column and sortable metrics are critical for traders to understand what parameter values produced which results. Clickable trial IDs dramatically speed up the workflow of re-running interesting trials as standalone backtests.

**Independent Test**: Can be tested by viewing any completed optimization's trial table, verifying the Params column displays correctly, sorting by each metric column, and clicking a trial ID to confirm the side panel opens with correct pre-populated parameters.

**Acceptance Scenarios**:

1. **Given** a completed optimization run with trials, **When** the trials table loads, **Then** each row includes a "Params" column as the last column, showing a CSV string of parameter key:value pairs (e.g., `Period:20, Threshold:1.5, Mode:FollowTrend`).
2. **Given** a trials table with multiple rows, **When** the user clicks any metric column header (Fitness, Sharpe, Sortino, Profit Factor, Max Drawdown, Win Rate, Trades, Net Profit), **Then** the table sorts by that column in ascending or descending order.
3. **Given** a trial row in the table, **When** the user clicks the trial ID, **Then** a backtest launch side panel opens with the same strategy, DSS, and parameter values pre-populated in the JSON editor, requiring only a click of the "Start" button to launch.
4. **Given** the backtest results table, **When** it loads, **Then** it also includes the Params column and supports sorting by all metric columns, consistent with the optimization trials table.

---

### User Story 4 - Cross-DSS Comparison Table (Priority: P4)

A new tab is available within an optimization group that shows all trials from all DSS runs in a single combined table. This cross-DSS table allows the trader to compare the best results across all assets side by side. Trials are initially grouped by DSS but can be sorted by any metric column to find, for example, the single best Sharpe ratio across all assets.

**Why this priority**: Cross-asset comparison is one of the primary motivations for the redesign. While per-DSS isolation preserves asset coverage, traders still need a unified view to compare relative performance across their portfolio.

**Independent Test**: Can be tested by viewing a completed optimization group and navigating to the cross-DSS tab, verifying that all trials from all DSS runs appear and can be sorted by any metric.

**Acceptance Scenarios**:

1. **Given** a completed optimization group with 3 DSS runs (each with trials), **When** the user navigates to the cross-DSS tab, **Then** a combined table displays all trials from all 3 DSS runs, initially grouped by DSS.
2. **Given** the cross-DSS table is displayed, **When** the user sorts by any metric column (e.g., Sharpe), **Then** the table re-sorts all trials across all DSS by that metric, breaking the initial DSS grouping.
3. **Given** the cross-DSS table, **When** the user clicks a trial ID, **Then** the same backtest launch side panel opens with the trial's parameters and DSS pre-populated (same behavior as in Story 3).
4. **Given** the cross-DSS table, **Then** it includes the same "Params" column as the per-DSS trial table.

---

### User Story 5 - DSS Editor in Optimization and Backtest Creation (Priority: P5)

In the "+New Optimization" form, a collapsible visual DSS builder appears above the existing parameters JSON editor. This builder provides a table/form where the user adds rows with AssetName, Exchange, and TimeFrame fields to define the list of data subscription sets. The builder auto-populates the `subscriptionAxis` field in the main JSON editor. Similarly, the "+New Backtest" form (non-debug mode) provides an option to select data subscriptions, enabling the user to run the same backtest parameters across multiple selected DSS.

**Why this priority**: The DSS editor is the input mechanism that makes per-DSS optimization possible from the UI. Without it, users would have to manually construct the request. The backtest DSS support complements it for ad-hoc multi-asset backtesting.

**Independent Test**: Can be tested by opening the optimization creation form, verifying the DSS editor is visible and collapsible, entering subscription data, and confirming the optimization launches with the specified DSS.

**Acceptance Scenarios**:

1. **Given** the user opens the "+New Optimization" form, **When** the form loads, **Then** a collapsible visual DSS builder appears above the parameters JSON editor, pre-collapsed by default.
2. **Given** the DSS builder is expanded, **When** the user adds rows, **Then** each row contains AssetName, Exchange, and TimeFrame fields, and the `subscriptionAxis` field in the main JSON editor is automatically populated.
3. **Given** the user opens the "+New Backtest" form (non-debug mode), **When** the form loads, **Then** a DSS selector is available allowing the user to specify one or more data subscription sets to run the backtest against.
4. **Given** the user has entered parameters and selected 3 DSS in the backtest form, **When** they click "Start", **Then** the system launches 3 separate backtests (one per DSS) with identical parameters.

---

### User Story 6 - Per-DSS Validation with Group Reference (Priority: P6)

Validation can be launched per optimization DSS group. Each validation run references the original optimization group (the list of DSS launched together). On the existing validations tab, each entry links back to its source optimization group as a 1-to-1 relationship. A new cross-DSS validation tab displays all validation trials from all DSS within a group, with the same sorting, Params column, and clickable trial ID capabilities as the cross-DSS optimization tab.

**Why this priority**: Validation is the natural follow-up step after optimization. Without DSS-aware validation, the per-DSS optimization results cannot be properly verified across out-of-sample data.

**Independent Test**: Can be tested by completing an optimization group, launching validation from it, and verifying that validation runs are created per DSS with group-level tracking and cross-DSS comparison.

**Acceptance Scenarios**:

1. **Given** a completed optimization group with 3 DSS, **When** the user launches validation for the group, **Then** the system creates validation runs per DSS, each validating the top N trials by fitness (capped at `MaxTrialsToValidate`, default 100), all referencing the source optimization group.
2. **Given** the validations tab, **When** it loads, **Then** each validation entry shows a 1-to-1 reference to its source optimization DSS group.
3. **Given** a completed validation group, **When** the user navigates to the cross-DSS validation tab, **Then** a combined table shows all validation trials from all DSS, with the same capabilities as the cross-DSS optimization tab (sortable metrics, Params column, clickable trial IDs).
4. **Given** a validation group, **When** the user sorts the cross-DSS validation table by a metric, **Then** all validation trials re-sort across DSS boundaries, the same as the optimization cross-DSS tab.

---

### Edge Cases

- What happens when an optimization is launched with only a single DSS? The system creates an optimization group with one run. Cross-DSS tab still functions but shows only one group.
- What happens when all DSS runs in a group fail? The group aggregate status shows "Failed" with individual error details per DSS run.
- What happens when some DSS runs succeed and others fail? The group aggregate status shows "PartiallyCompleted." Successful runs' results remain fully accessible.
- What happens when the user cancels an optimization group mid-run? All in-progress DSS runs within the group are cancelled. Already-completed DSS runs retain their results.
- How does the system handle a DSS with no valid historical data? That individual DSS run fails with an appropriate error, while other DSS runs in the group continue.
- What happens when sorting the cross-DSS table and two trials from different DSS have identical metric values? The table uses a stable secondary sort (DSS name, then trial ID) for deterministic ordering.
- What happens to existing optimization data in the database? Existing data is dropped. Schema changes are applied cleanly without migration.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST remove data subscriptions from the optimization parameter axes and instead treat each DSS as an independent optimization run.
- **FR-002**: System MUST create a new "Optimization Group" entity that logically groups all DSS optimization runs launched together in a single user action.
- **FR-003**: Each DSS optimization run MUST execute independently, including independent genetic populations, fitness ranking, and progress tracking.
- **FR-004**: Results for each DSS optimization run MUST be accessible immediately upon that run's completion, without waiting for other DSS runs in the same group.
- **FR-005**: The optimization trials table MUST include a "Params" column as the last column, displaying a CSV-formatted string of parameter key:value pairs for each trial.
- **FR-006**: The optimization trials table and backtest table MUST support sorting by all metric columns.
- **FR-007**: Each trial ID in the trials table MUST be clickable, opening a backtest launch side panel with the trial's strategy, DSS, and parameters pre-populated in the JSON editor.
- **FR-008**: System MUST provide a cross-DSS tab within an optimization group, displaying a combined table of all trials from all DSS runs, initially grouped by DSS, sortable by any metric.
- **FR-009**: The "+New Optimization" form MUST include a collapsible visual DSS builder (table/form for adding asset/exchange/timeframe rows) positioned above the parameters JSON editor, which auto-populates the `subscriptionAxis` field in the main JSON editor.
- **FR-010**: The "+New Backtest" form (non-debug mode) MUST provide an option to select data subscription sets, launching one backtest per selected DSS with identical parameters.
- **FR-011**: Validation MUST be launchable per optimization DSS group, with each validation referencing the source optimization group. Validation MUST run the top N trials per DSS by fitness, where N is capped at a configurable `MaxTrialsToValidate` (default 100).
- **FR-012**: The validations tab MUST show a 1-to-1 reference between each validation entry and its source optimization DSS group.
- **FR-013**: System MUST provide a cross-DSS validation tab with the same combined table capabilities as the cross-DSS optimization tab (sortable metrics, Params column, clickable trial IDs).
- **FR-014**: System MUST drop existing optimization data from the database rather than performing data migrations when changing the persistence schema.
- **FR-015**: Cancellation of an optimization group MUST cancel all in-progress DSS runs within the group while preserving results of already-completed DSS runs.
- **FR-016**: Failure of one DSS run within a group MUST NOT affect the execution of other DSS runs in the same group.
- **FR-017**: The cross-DSS table MUST include the same Params column and clickable trial ID behavior as the per-DSS trial table.
- **FR-018**: DSS runs within a group MUST be scheduled round-robin across the shared parallelism pool, ordered by DSS list position, so that all DSS runs make concurrent progress.

### Key Entities

- **Optimization Group**: A logical grouping of optimization runs launched together across a list of data subscription sets. Holds a unique group ID, the originating strategy, backtest settings, and references to all child optimization runs. Aggregate status stored and updated on each child completion: "InProgress" (any running), "Completed" (all succeeded), "PartiallyCompleted" (mixed success/failure/cancellation with at least one success), "Failed" (all failed), "Cancelled" (all cancelled with none completed).
- **Optimization Run**: An individual optimization (brute-force or genetic) for a single DSS within a group. Contains its own progress, trials, fitness rankings, and error state. Related to exactly one group and one DSS.
- **Data Subscription Set (DSS)**: A set of one or more data subscriptions (asset + exchange + timeframe) representing the market data context for a single optimization run. For single-asset strategies this is one subscription; for multi-asset strategies it is a set of subscriptions.
- **Trial**: A single parameter combination evaluation within an optimization run. Records parameter values, performance metrics, and fitness score. Belongs to exactly one optimization run.
- **Validation Group**: A set of validation runs launched from an optimization group. References the source optimization group as a 1-to-1 relationship. Contains child validation runs per DSS mirroring the optimization group structure.

## Clarifications

### Session 2026-04-12

- Q: How is the parallelism budget scoped across DSS runs within a group? → A: Shared — all DSS runs share one `MaxDegreeOfParallelism` pool (e.g., 4 total concurrent backtests across all DSS runs, not 4 per DSS).
- Q: How should optimization groups appear in the optimization list? → A: Groups as primary rows, expandable/collapsible to reveal individual DSS runs nested within.
- Q: What is the group aggregate status when DSS runs have mixed states? → A: Composite — "Completed" (all done), "PartiallyCompleted" (mixed success/failure), "Failed" (all failed). "InProgress" while any run is still executing.
- Q: Which trials from each DSS run get validated? → A: Top N trials per DSS run by fitness, capped at `MaxTrialsToValidate` (default 100) to prevent excessive validation runtime.
- Q: How should DSS runs within a group be scheduled across the shared parallelism pool? → A: Round-robin interleaved, ordered by the DSS list position as specified in the optimization launch options. All DSS runs make concurrent progress.

## Assumptions

- A "data subscription set" for a single-asset strategy contains exactly one DataSubscription. For multi-asset strategies, it contains the full set of subscriptions needed by that strategy.
- The DSS list is provided explicitly by the user in the new DSS editor; the system does not auto-discover which assets a strategy should optimize over.
- Genetic optimization hyper-parameters (population size, generations, etc.) are shared across all DSS runs within a group, not configured per-DSS.
- `MaxDegreeOfParallelism` is a shared budget across all DSS runs within a group. If set to 4, at most 4 backtests execute concurrently across all DSS runs combined, not 4 per DSS run.
- DSS runs are scheduled round-robin across the shared parallelism pool, ordered by the DSS list position as provided in the optimization launch options. This ensures all DSS runs make concurrent progress rather than executing sequentially.
- The Params column CSV format uses the pattern `Key:Value` with comma-space separation (e.g., `Period:20, Threshold:1.5`).
- The collapsible DSS builder in the optimization form is pre-collapsed by default to save screen space for users who are only modifying parameters. It provides a visual row-based interface (not raw JSON) that auto-populates `subscriptionAxis` in the main JSON editor. Power users can edit the JSON directly.
- "Sortable by all metric columns" refers to the existing metric columns (Fitness, Sharpe, Sortino, Profit Factor, Max Drawdown, Win Rate, Trades, Net Profit) plus the new Params column is not sortable (it is a display-only string).
- The side panel for launching a backtest from a trial reuses the existing backtest creation form/components, pre-filled with the trial's data.
- Validation defaults to the top 100 trials per DSS by fitness. The user can override this via `MaxTrialsToValidate` in the validation request to prevent excessive runtime from the 8-stage validation pipeline.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can view optimization results for the first completed DSS within seconds of that DSS run finishing, without waiting for other DSS runs in the same group.
- **SC-002**: 100% of optimization trials display their parameter key:value pairs in the Params column across all table views (per-DSS, cross-DSS, validation).
- **SC-003**: Users can sort trial tables by any metric column in both ascending and descending order, with the sorted view reflecting within 1 second for tables up to 10,000 trials.
- **SC-004**: Users can launch a backtest from any trial in 2 clicks or fewer (click trial ID, then click Start in the pre-populated side panel).
- **SC-005**: Cross-DSS comparison tables display all trials from all DSS runs in a group, supporting unified sorting that breaks DSS grouping boundaries.
- **SC-006**: Validation groups maintain a traceable 1-to-1 reference to their source optimization group, visible in the validations tab.
- **SC-007**: Cancellation or failure of any single DSS run does not interrupt, delay, or corrupt results of other DSS runs in the same group.
