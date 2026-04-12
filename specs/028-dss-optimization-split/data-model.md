# Data Model: Per-DSS Optimization Split

**Branch**: `028-dss-optimization-split` | **Date**: 2026-04-13

## Entity Overview

```
OptimizationGroup 1──N OptimizationRun 1──N BacktestRun (Trial)
       │                      │
       │                      └──N OptimizationFailedTrial
       │
       └──1 ValidationGroup 1──N ValidationRun 1──N ValidationStageResult
```

---

## New Entities

### OptimizationGroup

Logical grouping of optimization runs launched together across a list of DSS.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| id | TEXT (GUID) | PK | Unique group identifier |
| strategy_name | TEXT | NOT NULL | Strategy being optimized |
| strategy_version | TEXT | NULL | Resolved after first trial |
| optimization_method | TEXT | NOT NULL | "BruteForce" or "Genetic" |
| started_at | TEXT (ISO 8601) | NOT NULL | Group launch timestamp |
| completed_at | TEXT (ISO 8601) | NULL | Set when all children finish |
| total_runs | INTEGER | NOT NULL | Number of DSS runs in group |
| input_json | TEXT | NULL | Full original request JSON for re-run |
| subscriptions_json | TEXT | NOT NULL | JSON array of all DSS in the group |
| backtest_settings_json | TEXT | NOT NULL | Shared backtest settings |
| optimization_settings_json | TEXT | NULL | Shared optimization/genetic settings |
| fitness_config_json | TEXT | NULL | Shared fitness config |
| max_parallelism | INTEGER | NOT NULL | Shared parallelism budget |
| status | TEXT | NOT NULL DEFAULT 'InProgress' | Aggregate status, updated on each child completion |

**Status**: Stored on the group row, updated each time a child run completes.

**Status update rules**:
- Any child InProgress → "InProgress"
- All Completed → "Completed"  
- Mixed (≥1 Completed, ≥1 Failed/Cancelled, 0 InProgress) → "PartiallyCompleted"
- All Failed → "Failed"
- All Cancelled (0 Completed) → "Cancelled"

**Uniqueness**: `id` is unique. Groups are deduplicated by a RunKey (SHA256 hash of strategy + backtest settings + DSS list + axes + optimization method). Submitting identical parameters while a group is in-progress returns the existing group.

---

### OptimizationRun (Modified)

Existing `optimization_runs` table. New column added.

| New Field | Type | Constraints | Description |
|-----------|------|-------------|-------------|
| group_id | TEXT (GUID) | NOT NULL, FK → optimization_groups(id) | Parent group |

**Removed fields**: `subscriptions_json` is moved to group level. Each run's DSS is identified by its index into the group's `subscriptions_json` array. The existing `asset_name`, `exchange`, `timeframe` columns remain for the primary subscription (display/filtering).

| New Field | Type | Constraints | Description |
|-----------|------|-------------|-------------|
| dss_index | INTEGER | NOT NULL | Index into the parent group's `subscriptions_json` array |

**Lifecycle states** (unchanged): InProgress → Completed | Failed | Cancelled

---

### ValidationGroup (New)

Logical grouping of validation runs launched from an optimization group.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| id | TEXT (GUID) | PK | Unique validation group identifier |
| optimization_group_id | TEXT (GUID) | NOT NULL, FK → optimization_groups(id) | Source optimization group |
| strategy_name | TEXT | NOT NULL | Copied from optimization group |
| threshold_profile_name | TEXT | NOT NULL | Shared threshold profile |
| threshold_profile_json | TEXT | NULL | JSON of resolved profile |
| started_at | TEXT (ISO 8601) | NOT NULL | Group launch timestamp |
| completed_at | TEXT (ISO 8601) | NULL | Set when all children finish |
| total_runs | INTEGER | NOT NULL | Number of DSS validation runs |
| status | TEXT | NOT NULL DEFAULT 'InProgress' | Aggregate status, updated on each child completion |

**Status**: Stored on the group row, updated each time a child validation run completes. Same rules as optimization groups.

---

### ValidationRun (Modified)

Existing `validation_runs` table. New column added.

| New Field | Type | Constraints | Description |
|-----------|------|-------------|-------------|
| validation_group_id | TEXT (GUID) | NOT NULL, FK → validation_groups(id) | Parent validation group |

The existing `optimization_run_id` FK remains, pointing to the specific per-DSS optimization run.

---

## Modified Tables Summary

### optimization_groups (NEW)

```sql
CREATE TABLE optimization_groups (
    id                          TEXT PRIMARY KEY,
    strategy_name               TEXT NOT NULL,
    strategy_version            TEXT,
    optimization_method         TEXT NOT NULL,
    started_at                  TEXT NOT NULL,
    completed_at                TEXT,
    total_runs                  INTEGER NOT NULL,
    status                      TEXT NOT NULL DEFAULT 'InProgress',
    input_json                  TEXT,
    subscriptions_json          TEXT NOT NULL,
    backtest_settings_json      TEXT NOT NULL,
    optimization_settings_json  TEXT,
    fitness_config_json         TEXT,
    max_parallelism             INTEGER NOT NULL
);
```

### optimization_runs (MODIFIED)

```sql
-- New columns added:
ALTER TABLE optimization_runs ADD COLUMN group_id TEXT NOT NULL REFERENCES optimization_groups(id);
ALTER TABLE optimization_runs ADD COLUMN dss_index INTEGER NOT NULL DEFAULT 0;

-- New index:
CREATE INDEX ix_or_group_id ON optimization_runs(group_id);
```

Note: Since FR-014 mandates dropping existing data, these are applied as part of a full schema recreate, not ALTER statements.

### validation_groups (NEW)

```sql
CREATE TABLE validation_groups (
    id                      TEXT PRIMARY KEY,
    optimization_group_id   TEXT NOT NULL REFERENCES optimization_groups(id),
    strategy_name           TEXT NOT NULL,
    threshold_profile_name  TEXT NOT NULL,
    threshold_profile_json  TEXT,
    started_at              TEXT NOT NULL,
    completed_at            TEXT,
    total_runs              INTEGER NOT NULL,
    status                  TEXT NOT NULL DEFAULT 'InProgress'
);

CREATE INDEX ix_vg_opt_group_id ON validation_groups(optimization_group_id);
```

### validation_runs (MODIFIED)

```sql
-- New column added:
ALTER TABLE validation_runs ADD COLUMN validation_group_id TEXT NOT NULL REFERENCES validation_groups(id);

-- New index:
CREATE INDEX ix_vr_validation_group_id ON validation_runs(validation_group_id);
```

---

## BacktestRun (Trial) — Schema Changes

### Denormalized Metric Columns (Issue #3)

To support efficient cross-DSS sorting on up to 100K rows, sortable metrics are denormalized as real columns on `backtest_runs`. These are populated at write time from `PerformanceMetrics` alongside the existing `fitness_score` column. `metrics_json` is retained for the full detail view.

```sql
-- New columns on backtest_runs (applied as part of full schema recreate):
ALTER TABLE backtest_runs ADD COLUMN sharpe_ratio REAL;
ALTER TABLE backtest_runs ADD COLUMN sortino_ratio REAL;
ALTER TABLE backtest_runs ADD COLUMN profit_factor REAL;
ALTER TABLE backtest_runs ADD COLUMN max_drawdown_pct REAL;
ALTER TABLE backtest_runs ADD COLUMN win_rate_pct REAL;
ALTER TABLE backtest_runs ADD COLUMN total_trades INTEGER;
ALTER TABLE backtest_runs ADD COLUMN net_profit REAL;
ALTER TABLE backtest_runs ADD COLUMN annualized_return_pct REAL;
```

Cross-DSS trials queries can now use column-based ORDER BY:
```sql
SELECT br.*, or_.group_id, or_.dss_index
FROM backtest_runs br
JOIN optimization_runs or_ ON br.optimization_run_id = or_.id
WHERE or_.group_id = ?
ORDER BY br.sharpe_ratio DESC
LIMIT 1000 OFFSET 0
```

### Params Column Display

No schema change needed. The `parameters_json` field already stores the full parameter dictionary. The CSV display string is computed at read time:

**Derivation**: `parameters_json` → parse JSON → filter out internal keys (`DataSubscriptions`) → format each remaining key-value as `Key:Value` → join with `, `.

For module parameters: `ModuleSlotKey:VariantTypeKey` (sub-params omitted from CSV for brevity).

---

## Data Volume Assumptions

| Entity | Expected Scale | Notes |
|--------|---------------|-------|
| OptimizationGroup | 10-100 per strategy | One per multi-DSS launch |
| OptimizationRun | 3-10 per group | One per DSS |
| Trials (BacktestRun) | Up to MaxTrialsToKeep per run (default 10,000) | Top-N by fitness |
| ValidationGroup | 1 per optimization group | User-initiated |
| ValidationRun | 3-10 per validation group | Mirrors DSS count |

---

## State Transition Diagrams

### Optimization Group Lifecycle

```
[Created] ──(all children started)──→ [InProgress]
                                          │
                    ┌─────────────────────┼─────────────────────┐
                    ▼                     ▼                     ▼
              [Completed]        [PartiallyCompleted]       [Failed]
            (all succeeded)    (mixed success/failure)    (all failed)
                                                              │
                                                              ▼
                                                         [Cancelled]
                                                       (all cancelled,
                                                        none completed)
```

### Individual Run Lifecycle (unchanged)

```
[InProgress] ──(success)──→ [Completed]
     │
     ├──(exception)──→ [Failed]
     │
     └──(user cancel)──→ [Cancelled]
```
