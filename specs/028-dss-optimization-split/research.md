# Research: Per-DSS Optimization Split

**Branch**: `028-dss-optimization-split` | **Date**: 2026-04-13

## Research Summary

All NEEDS CLARIFICATION items have been resolved through codebase research and the clarification session. No external dependencies or unknown technologies are involved.

---

## R1: Current SubscriptionAxis Architecture

**Decision**: Remove `SubscriptionAxis` from the parameter axes and instead fan out into N independent optimization runs.

**Rationale**: Currently, `OptimizationSetupHelper.AppendSubscriptionAxisAndFilter()` appends subscriptions as a `ResolvedDiscreteAxis("DataSubscriptions", ...)` into the Cartesian product. This means all DSS combinations are multiplied with all parameter combinations into a single run. The new design intercepts *before* the axis append — the DSS list is consumed by the group orchestrator to create N independent runs, each receiving its own single DSS (no subscription axis at all).

**Alternatives considered**:
- Keeping the axis but saving results per-DSS incrementally → rejected because genetic mode needs per-DSS populations, not cross-DSS genetic pressure.
- Creating a "sub-run" concept within a single run → rejected because it adds complexity without clean separation; independent runs with a grouping entity is simpler.

**Note**: Per-DSS execution is handled by the group handler's channel consumers calling `OptimizationSetupHelper.ExecuteTrial()` directly, not by dispatching to existing per-run command handlers. The existing `RunOptimizationCommandHandler` and `RunGeneticOptimizationCommandHandler` remain unchanged for potential standalone use.

---

## R2: Shared Parallelism Model

**Decision**: Use a shared `Channel<(int DssIndex, ParameterCombination Combo)>` work queue with `maxParallelism` consumer tasks.

**Rationale**: The group handler creates a single bounded `Channel<T>` and spawns exactly `maxParallelism` LongRunning consumer tasks — matching the existing pattern of one dedicated thread per concurrent slot.

For brute-force: a producer loop round-robin enqueues combinations from each DSS's `IEnumerable<ParameterCombination>` into the channel (DSS[0] combo, DSS[1] combo, DSS[2] combo, DSS[0] combo, ...). When a DSS is exhausted, it's removed from the rotation. Consumer tasks pull `(DssIndex, Combo)` tuples and call `helper.ExecuteTrial()` using the DssIndex-specific data cache. Per-DSS `BoundedTrialQueue` and progress counters are indexed by DssIndex.

For genetic: each DSS maintains its own GA population and evolution loop. During the evaluation phase, a DSS pushes its population's work items (as `(DssIndex, Combo)` tuples) into the shared channel. A per-DSS `ManualResetEventSlim` signals when all items for that generation are evaluated, so the DSS can evolve to the next generation while other DSS runs continue evaluating. Evolution (selection, crossover, mutation) is CPU-light and doesn't use the channel.

This gives true deterministic round-robin for brute-force and fair interleaving for genetic, with exactly `maxParallelism` OS threads — no waste.

**Alternatives considered**:
- `SemaphoreSlim(maxParallelism)` shared across N independent per-DSS partitioner loops → rejected because N DSS runs × maxParallelism LongRunning tasks creates N×maxParallelism dedicated OS threads, but only maxParallelism can hold the semaphore at any time. The remaining threads block on `semaphore.Wait()` doing nothing — pure thread waste. Also provides only approximate, not deterministic, round-robin.
- Per-DSS parallelism budgets (divide maxParallelism by N) → unfair when DSS runs finish at different times; wastes capacity.
- `TaskScheduler` with concurrency limit → more complex, doesn't integrate cleanly with the existing partitioner-based trial execution.

---

## R3: Group Status Persistence

**Decision**: Store both `status` and `completed_at` on the group row, updated when each child run completes.

**Rationale**: The group handler updates the group row each time a child run finishes. The update logic applies composite rules:
- "InProgress" — any child run is InProgress
- "Completed" — all children are Completed
- "PartiallyCompleted" — at least one Completed and at least one Failed/Cancelled (none InProgress)
- "Failed" — all children are Failed (none InProgress, none Completed)
- "Cancelled" — all children are Cancelled (none InProgress, none Completed)

When the last child finishes, `completed_at` is set to the current timestamp. A single `UPDATE optimization_groups SET status = ..., completed_at = ... WHERE id = ...` per child completion is trivial. This avoids needing a subquery or JOIN to compute status on every list query.

**Alternatives considered**:
- Derive on read (no stored status) → adds subquery overhead to every list call for negligible benefit. Also creates an inconsistency: `completed_at` would need to be derived too, but was originally designed as a stored column.
- Event-based status propagation → overengineered for single-node SQLite deployment.

---

## R4: Optimization Group Persistence

**Decision**: New `optimization_groups` table with child runs linked via `group_id` FK on existing `optimization_runs`.

**Rationale**: The group stores shared settings (strategy, backtest settings, input JSON, DSS list, fitness config) while each child run stores per-DSS results (trials, progress, errors). The `optimization_runs` table gains a `group_id` FK column. Groups without a group_id (null) are legacy solo runs — but since FR-014 mandates dropping existing data, all future runs will have a group_id.

**Alternatives considered**:
- Separate junction table (group_members) → adds complexity for a simple 1:N relationship.
- Storing group info as JSON blob on each child run → duplicates data, makes group-level queries harder.

---

## R5: Validation Group Design

**Decision**: New `validation_groups` table mirroring the optimization group pattern. Each validation group references its source optimization group. Child validation runs reference both the validation group and their per-DSS source optimization run.

**Rationale**: Validation currently takes a single `OptimizationRunId`. The new flow takes an `OptimizationGroupId`, creates a validation group, and spawns per-DSS validation runs. Each validation run still operates on trials from a single optimization run (1:1 per DSS).

**Alternatives considered**:
- Reusing the existing validation flow without grouping → loses cross-DSS comparison capability.
- Validating all DSS trials in a single validation run → contradicts the per-DSS isolation principle.

---

## R6: Frontend Group Display

**Decision**: Optimization list shows groups as expandable primary rows using a tree-table pattern.

**Rationale**: The current `RunsTable` component renders flat rows. The redesign adds a row expansion mechanism where clicking a group row reveals its child DSS runs as nested rows. Libraries like TanStack Table (already used via React Query in the project) support row expansion natively.

**Alternatives considered**:
- Accordion component → doesn't integrate well with table sorting/filtering.
- Separate group detail page → adds navigation overhead; inline expansion is faster for the trader.

---

## R7: Params Column Format

**Decision**: CSV format `Key:Value, Key:Value` derived from the trial's `parameters_json` stored in `backtest_runs`.

**Rationale**: The trial's `parameters_json` is already stored as a JSON blob. The Params column renders a human-readable summary by iterating the JSON object's key-value pairs. Module selections are formatted as `ModuleSlot:VariantName`. This is a display-only column (not sortable, not filterable).

**Alternatives considered**:
- Storing a pre-rendered CSV string in the DB → adds storage overhead for a derived value.
- Showing raw JSON → too verbose for a table column.

---

## R8: Trial-to-Backtest Side Panel

**Decision**: Reuse the existing `RunNewPanel` component, opened with pre-populated content via the `openWithContent()` context method.

**Rationale**: The frontend already has a `RunNewContext` with `openWithContent(content)` that populates the CodeMirror editor and opens the panel. Clicking a trial ID constructs a `RunBacktestRequest` from the trial's parameters and DSS, then calls `openWithContent()`. This reuses all existing form infrastructure (validation, template switching, submission).

**Alternatives considered**:
- Dedicated side panel component → duplicates form logic unnecessarily.
- Navigate to backtest page with session storage → adds a page transition; side panel is faster.

---

## R9: Cross-DSS Trial Sorting Performance

**Decision**: Denormalize sortable metrics as real columns on `backtest_runs`.

**Rationale**: The cross-DSS trials endpoint sorts across all trials from all DSS runs in a group — potentially 10 DSS × 10,000 trials = 100,000 rows. Currently, most metrics are stored only in `metrics_json` (a JSON blob), with only `fitness_score` as a real column. Sorting 100K rows by extracting values from JSON (`json_extract()`) is too slow in SQLite.

Denormalized columns: `sharpe_ratio`, `sortino_ratio`, `profit_factor`, `max_drawdown_pct`, `win_rate_pct`, `total_trades`, `net_profit`, `annualized_return_pct`. These are populated at write time from `PerformanceMetrics` alongside the existing `fitness_score` column. `metrics_json` is retained for the full detail view but is not used for sorting.

**Alternatives considered**:
- Sort via `json_extract()` on `metrics_json` → too slow on 100K rows; no index support for JSON paths in SQLite.
- Client-side sorting (fetch all, sort in browser) → unacceptable for 100K rows; pagination breaks.
- Pre-computed materialized view → SQLite doesn't support materialized views.

---

## R10: DSS Input UX — Visual Builder

**Decision**: Use a collapsible visual DSS builder (table/form for adding asset/exchange/timeframe rows) instead of a second JSON editor.

**Rationale**: The spec originally called for a separate JSON editor for data subscriptions above the params editor. This creates UX friction: two editors to manage, mental context switching, and re-run from `inputJson` must split JSON across two editors. A visual row builder (add/remove rows with AssetName, Exchange, TimeFrame fields) auto-populates the `subscriptionAxis` field in the main JSON editor. Power users can still edit `subscriptionAxis` directly in the JSON.

**Alternatives considered**:
- Second JSON editor for DSS → requires splitting/merging JSON on re-run, confusing UX for a structured input (DSS is just a list of 3-field tuples).
- Dropdown selectors populated from available assets → good future enhancement but requires asset discovery API; row builder is the right MVP.

---

## R11: Group-Level Deduplication

**Decision**: Extend `RunKeyBuilder` to produce a group-level key. Check for existing in-progress groups before creating a new one.

**Rationale**: Individual optimization runs use `RunKeyBuilder.Build()` to prevent duplicate submissions via SHA256 hashing of command parameters. Without equivalent dedup for groups, double-clicking "Run" or network retries create duplicate groups with N×2 child runs. The group key hashes: strategy name + backtest settings + DSS list + optimization axes + optimization method.

When a group-level key collision is found with an in-progress group, the handler returns the existing group's submission DTO — same pattern as the existing per-run dedup.

**Alternatives considered**:
- Frontend-only dedup (disable button after click) → insufficient; doesn't protect against network retries or concurrent API calls.
- No dedup (groups are always new) → risks wasting compute on accidental duplicates.
