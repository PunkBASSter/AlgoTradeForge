# Technical Requirements Document: Overfitting Detection Pipeline

## Context

AlgoTradeForge's optimization engine produces hundreds to thousands of parameter combinations ranked by fitness score, but has no mechanism to distinguish genuinely robust strategies from curve-fitted artifacts. The PRD (`docs/overfitting-detection-requirements.md`) specifies a multi-stage validation pipeline implementing 12+ complementary techniques — from instant sanity checks to multi-hour statistical audits — that progressively filter candidates before live deployment. This TRD defines the implementation architecture for that pipeline within AlgoTradeForge's existing clean architecture.

---

## 1. Implementation Approach

### 1.1 Integration Model: Post-Optimization Validation Command

The validation pipeline is a **separate command** (`RunValidationCommand`) triggered after an optimization completes, referencing the source `OptimizationRunId`. It is **not embedded** in the optimization flow for three reasons:

1. **Separation of concerns.** Optimization produces candidates; validation filters them. Coupling them forces every optimization to pay validation cost, even during exploratory runs.
2. **Replayability.** A user can re-validate the same optimization with different threshold profiles without re-running the optimization.
3. **Existing pattern alignment.** Follows the same submit → 202 Accepted → poll progress → fetch results pattern used by `RunOptimizationCommandHandler` and `RunBacktestCommandHandler`.

The pipeline reads from the completed `OptimizationRunRecord` and its `BacktestRunRecord` trials, then enters a multi-stage gate sequence where each stage passes survivors forward or rejects them with a reason code.

### 1.2 The Simulation Cache: Architectural Linchpin

The simulation cache transforms WFO/WFM from O(windows × trials × bars) backtest invocations into O(1) matrix slicing. It is the single most impactful optimization.

**How it works with the existing engine:** `BacktestEngine.Run()` already produces `IReadOnlyList<EquitySnapshot>` (one per bar). The per-bar P&L delta is simply `equity[i] - equity[i-1]`. No engine changes needed — we post-process the existing output.

**Critical prerequisite:** Currently `OptimizationSetupHelper.ExecuteTrial()` sets `EquityCurve = []` for optimization trials — equity curves are discarded. The optimization handler must be modified to **retain equity curves for trials that pass the initial filter** (the top-N stored in `BoundedTrialQueue`). This is the one required change to the existing optimization flow.

**Cache population timing:** Built as a separate step after loading optimization results but before validation stages 4–7. The `SimulationCacheBuilder` loads all trial equity curves from SQLite, computes deltas, and assembles the T×N matrix.

### 1.3 Progressive Gate Pattern

Each stage implements `IValidationStage` with a single `ExecuteAsync(ValidationContext, CancellationToken)` method. The pipeline orchestrator iterates stages in order, passing only survivors forward. If a stage eliminates all remaining candidates, subsequent stages are skipped. Progress is reported per-stage via the existing `RunProgressCache`.

---

## 2. Data Structures

### 2.1 Simulation Cache (Domain)

```
SimulationCache
├── BarTimestamps: long[]                  // T shared timestamps
├── TrialPnlMatrix: double[N][T]           // Per-bar P&L delta per trial (row-major)
├── TrialParameters: ParameterCombination[] // Maps trial index → params
├── TrialMetrics: PerformanceMetrics[]      // Pre-computed metrics per trial
├── TrialTrades: ClosedTrade[][]            // Per-trial trade list (for bootstrap)
├── TrialCount (N), BarCount (T)
│
├── SliceWindow(startBar, endBar)          // → ReadOnlySpan view into matrix
├── GetTrialPnl(trialIndex)               // → ReadOnlySpan<double> (one trial, all bars)
├── GetBarPnl(barIndex)                    // → column slice across all trials
└── ComputeMetricsForWindow(trial, start, end) // → PerformanceMetrics for sub-window
```

**Memory estimate:** 10,000 bars × 1,000 trials = 10M doubles ≈ 80 MB (in-memory). For larger runs (100K+ combinations), automatic spillover to memory-mapped files via `System.IO.MemoryMappedFiles` (in-box).

**Why jagged arrays (`double[][]`) over 2D arrays:** (a) Better cache locality for row-wise access (the dominant pattern: one trial across time), (b) compatible with `Span<double>` for zero-allocation slicing, (c) no contiguous allocation requirement for large matrices.

### 2.2 Validation Pipeline State (Application/Persistence)

```
ValidationRunRecord
├── Id: Guid
├── OptimizationRunId: Guid                // Source optimization
├── StrategyName, StrategyVersion
├── StartedAt, CompletedAt, DurationMs
├── Status: InProgress | Completed | Failed | Cancelled
├── ThresholdProfileName: string           // "Crypto-Conservative", etc.
├── ThresholdProfileJson: string           // Full snapshot of thresholds used
├── CandidatesIn, CandidatesOut: int       // Pipeline entry/exit counts
├── CompositeScore: double                 // 0–100
├── Verdict: Red | Yellow | Green
├── VerdictSummary: string                 // One-sentence human-readable
├── StageResults: List<StageResultRecord>
├── InvocationCount: int                   // Meta-overfitting tracking
└── ErrorMessage: string?

StageResultRecord
├── StageNumber (0–7), StageName
├── CandidatesIn, CandidatesOut
├── DurationMs
└── CandidateVerdicts: List<CandidateVerdict>

CandidateVerdict
├── TrialId: Guid
├── Passed: bool
├── ReasonCode: string?                    // e.g. "WFE_BELOW_THRESHOLD"
└── Metrics: Dictionary<string, double>    // Stage-specific computed metrics
```

### 2.3 Validation Configuration

```
ValidationThresholdProfile
├── Name: string
├── Stage1: MinNetProfit, MinProfitFactor(1.05), MinTradeCount(30),
│           MinTStatistic(2.0), MaxDrawdownPct(40)
├── Stage2: DsrPValue(0.05), MinPsr(0.95), MinProfitFactor(1.20),
│           MinRecoveryFactor(1.5), MinSharpe(0.5)
├── Stage3: MaxDegradationPct(30), MinClusterConcentration(0.50),
│           SensitivityIterations(500), SensitivityRange(0.10)
├── Stage4: MinWfe(0.50), MinProfitableWindowsPct(0.70),
│           MaxOosDrawdownExcess(0.50), MinWfoRuns(5), OosPct(0.20)
├── Stage5: PeriodCounts[4,6,8,10,12,15], OosPcts[0.15,0.20,0.25],
│           MinContiguousCluster(3×3), MinCellsPassing(7)
├── Stage6: BootstrapIter(1000), MaxDdMultiplier(1.5), PermutationIter(1000),
│           MinPermutationPValue(0.05), NoiseIter(500), MinNoiseRetention(0.70),
│           CostStressMultiplier(2.0)
├── Stage7: CscvBlocks(16), MaxPbo(0.30), SpaReplications(1000),
│           MinSpaPValue(0.05), MinProfitableSubPeriods(0.70), MinR2(0.85)
└── SafetyFloors: MinTradeCount(30), MaxPbo(0.60), MinWfe(0.30)
    // Cannot be relaxed below these regardless of profile
```

**Built-in profiles:**
- **Crypto-Conservative:** PBO<0.30, WFE≥0.60, TradeCount≥200, CostStress 3×
- **Crypto-Standard:** PBO<0.40, WFE≥0.50, TradeCount≥100, CostStress 2×
- **Custom:** User-configurable with safety floor enforcement

### 2.4 Per-Technique Result Models (Domain)

```
WfoResult                              WfmResult
├── Windows: List<WfoWindowResult>     ├── Grid: WfoResult[periods][oosPcts]
├── StitchedOosEquity: double[]        ├── PeriodCounts: int[]
├── WalkForwardEfficiency: double      ├── OosPcts: double[]
├── ProfitableWindowsPct: double       ├── LargestContiguousCluster: (r,c,rows,cols)
└── ParameterStability: double         ├── ClusterPassCount: int
                                       └── OptimalReoptPeriod: int
WfoWindowResult
├── WindowIndex, IsStart/End, OosStart/End (bar indices)
├── IsMetrics, OosMetrics: PerformanceMetrics
├── OptimalParameters: ParameterCombination
└── Wfe: double

PboResult                              MonteCarloResult
├── Pbo: double                        ├── DrawdownPercentiles: Dict<int, double>
├── LogitDistribution: double[]        ├── EquityFanBands: double[][]
├── NumCombinations, NumBlocks         └── ProbabilityOfRuin: double

PermutationTestResult                  ParameterSensitivityResult
├── PValue: double                     ├── MeanSharpeRetention: double
├── OriginalMetric: double             └── Heatmaps: List<ParameterHeatmap>
└── PermutedDistribution: double[]
                                       ParameterHeatmap
ClusterAnalysisResult                  ├── Param1Name, Param2Name
├── PrimaryClusterConcentration        ├── Param1Values[], Param2Values[]
├── ClusterCount                       ├── FitnessGrid: double[,]
├── ClusterCentroid: ParameterCombo    └── PlateauScore: double
└── SilhouetteScore
                                       DsrResult
SubPeriodConsistencyResult             ├── DsrPValue, Psr
├── ProfitableSubPeriodsPct            ├── AdjustedSharpe
├── SharpeCoeffOfVariation             └── EffectiveTrialCount
├── EquityCurveR2
└── SubPeriodMetrics: List<(range, m)> RegimeAnalysisResult
                                       ├── Regimes: List<(label, range, metrics)>
DecayAnalysisResult                    ├── ProfitableRegimeCount
├── RollingSharpe: (ts, sharpe)[]      └── SharpeRange: double
├── SlopeCoefficient: double
└── IsDecaying: bool
```

### 2.5 Persistence Schema (SQLite, migration v12+)

```
validation_runs
├── id TEXT PK
├── optimization_run_id TEXT FK → optimization_runs(id)
├── strategy_name TEXT, started_at TEXT, completed_at TEXT, duration_ms INTEGER
├── status TEXT, threshold_profile TEXT, threshold_json TEXT
├── candidates_in INTEGER, candidates_out INTEGER
├── composite_score REAL, verdict TEXT, verdict_summary TEXT
├── invocation_count INTEGER, error_message TEXT NULL
└── INDEX on optimization_run_id, strategy_name

validation_stage_results
├── id TEXT PK
├── validation_run_id TEXT FK → validation_runs(id)
├── stage_number INTEGER, stage_name TEXT
├── candidates_in INTEGER, candidates_out INTEGER, duration_ms INTEGER
├── verdicts_json TEXT   -- serialized CandidateVerdict[]
├── results_json TEXT    -- serialized technique-specific result models
└── INDEX on validation_run_id

simulation_cache_metadata
├── optimization_run_id TEXT PK FK → optimization_runs(id)
├── bar_count INTEGER, trial_count INTEGER
├── cache_file_path TEXT  -- path to mmap file (null if in-memory only)
├── created_at TEXT, size_bytes INTEGER
└── -- Cache data itself stored as binary file, NOT in SQLite
```

**Why binary files for cache data:** SQLite BLOB columns have poor random-access performance. The cache can be tens of MB. Memory-mapped I/O gives zero-copy access through the same `Span<double>` API. SQLite tracks metadata only.

---

## 3. Functional Units

### 3.1 Domain Layer — `Domain/Validation/`

| Component | Responsibility |
|---|---|
| **`SimulationCache`** | T×N P&L matrix with zero-allocation window slicing via `Span<double>` |
| **`Statistics/DeflatedSharpeRatio`** | Closed-form DSR and PSR. Ported from Bailey & López de Prado (2014). Inputs: observed Sharpe, N trials, T length, skewness, kurtosis |
| **`Statistics/PboCalculator`** | CSCV/PBO: partitions T×N returns into S blocks, enumerates C(S,S/2) combos, computes fraction where IS-optimal ranks below OOS median. Parallelizable via `Parallel.For` |
| **`Statistics/MonteCarloBootstrap`** | Trade-level bootstrap (shuffle trades → synthetic equity curves → drawdown percentiles). Price permutation (log-return shuffle → exponentiate → synthetic series) |
| **`Statistics/WalkForwardEngine`** | WFO using SimulationCache slicing: per-window IS optimization + OOS evaluation. Also drives WFM by iterating over config grid |
| **`Statistics/ParameterSensitivityAnalyzer`** | Parameter perturbation grid (±range%), evaluates fitness from cache where possible |
| **`Statistics/ClusterAnalyzer`** | K-Means on top-N parameter sets, silhouette scoring, centroid extraction. Self-contained (no external lib needed for typical 2–10 dimensions) |
| **`Statistics/RegimeDetector`** | 60-day rolling volatility with percentile-based Bull/Bear/Sideways classification. Simple and fixed — not HMM — to avoid overfitting the detector itself (per PRD §10) |
| **`Statistics/SubPeriodAnalyzer`** | Decomposes equity curve into equal sub-periods, per-period metrics, R² via linear regression |
| **`Statistics/DecayAnalyzer`** | Rolling Sharpe time series + linear regression slope for alpha erosion detection |
| **`Scoring/CompositeScoreCalculator`** | Weighted aggregation (WFO 25%, Stats 20%, Params 15%, MC 15%, Regime 10%, SubPeriod 10%, Data 5%) + hard rejection rules → traffic-light verdict |

**Why Domain:** All are pure functions over data structures — no I/O, no DI, independently testable. Matches existing placement of `MetricsCalculator`, `CompositeFitnessFunction`, `CartesianProductGenerator`.

### 3.2 Application Layer — `Application/Validation/`

| Component | Responsibility |
|---|---|
| **`RunValidationCommand`** | Command record: OptimizationRunId, ThresholdProfileName, ThresholdOverrides, MaxDegreeOfParallelism |
| **`RunValidationCommandHandler`** | Orchestrator: validate → insert placeholder → build cache → run stages sequentially → compute composite → save results. Background `Task.Factory.StartNew(LongRunning)` pattern |
| **`SimulationCacheBuilder`** | Loads optimization trials from `IRunRepository`, extracts equity curves, computes P&L deltas, assembles `SimulationCache`. Handles memory/disk decision based on estimated size |
| **`IValidationStage`** | Interface: `StageNumber`, `StageName`, `ExecuteAsync(ValidationContext, CancellationToken) → StageResult` |
| **`ValidationContext`** | Bag threaded through stages: SimulationCache, ThresholdProfile, surviving candidates, IRunRepository, BacktestEngine ref, source OptimizationRunRecord, `Dictionary<string,object> StageData` for cross-stage communication |
| **`Stages/Stage0PreFlight` … `Stage7SelectionBiasAudit`** | Eight concrete stages. Each composes Domain calculators with I/O and progress reporting |
| **`ValidationThresholdProfiles`** | Static factory for built-in profiles. Merge logic for user overrides. Safety floor enforcement |
| **`GetValidationByIdQuery`**, **`ListValidationsQuery`**, **`GetValidationStatusQuery`** | Query handlers following existing patterns |

### 3.3 Infrastructure Layer

| Component | Responsibility |
|---|---|
| **`SqliteValidationRepository`** | CRUD for `ValidationRunRecord` and `StageResultRecord`. Follows existing WAL/parameterized patterns from `SqliteRunRepository` |
| **`SimulationCacheFileStore`** | Memory-mapped file backend for caches exceeding threshold (default 200 MB). Binary format: header + row-major doubles. Uses `System.IO.MemoryMappedFiles` (in-box) |
| **`SqliteDbInitializer` (modified)** | Migration v12+ adding `validation_runs`, `validation_stage_results`, `simulation_cache_metadata` tables |

### 3.4 WebApi Layer

| Endpoint | Description |
|---|---|
| `POST /api/validations` | Submit validation run → 202 Accepted + ValidationRunId |
| `GET /api/validations/{id}` | Full results: scorecard + all stage results |
| `GET /api/validations/{id}/status` | Progress polling (current stage, candidates remaining) |
| `GET /api/validations` | List validation runs with filters |
| `POST /api/validations/{id}/cancel` | Cancel in-progress validation |
| `DELETE /api/validations/{id}` | Delete validation run |

**Contracts:** `RunValidationRequest`, `ValidationRunResponse`, `ValidationStatusResponse`, `StageResultResponse`, `ScorecardResponse`

### 3.5 Frontend — `frontend/components/features/validation/`

| Component | Description |
|---|---|
| **`validation-scorecard`** | Traffic-light verdict, composite score (0–100), one-sentence summary, stage progression bar |
| **`validation-stage-detail`** | Expandable per-stage view with candidate pass/fail table and reason codes |
| **`wfm-heatmap`** | 6×3 grid colored by WFE, contiguous cluster highlighted |
| **`parameter-surface`** | 2D heatmap per parameter pair (fitness grid) |
| **`monte-carlo-fan`** | Equity fan chart with 5th/25th/50th/75th/95th percentile bands |
| **`pbo-distribution`** | Histogram of CSCV logit distribution |
| **`rolling-sharpe`** | Time series with zero-line, decay trend, red highlighting for periods below zero |
| **`monthly-returns-heatmap`** | Calendar grid (months × years) with green/red cells |
| **`equity-comparison`** | IS, OOS, and stitched WFO equity curves overlaid |

**Page:** `frontend/app/validations/[id]/page.tsx` — tabbed layout: **Scorecard | Stages | Charts**

---

## 4. Main Architectural Decisions

### AD-1: Domain vs. Application Placement of Statistical Techniques

**Decision:** All statistical calculators live in **Domain**. Stage orchestration and I/O live in **Application**.

**Rationale:** Statistical computations are pure functions — no I/O, no DI, independently testable. Matches existing placement of `MetricsCalculator`, `CompositeFitnessFunction`. Application stages compose Domain calculators with persistence, progress, and cancellation.

### AD-2: Simulation Cache — Memory vs. Disk

**Decision:** In-memory by default, automatic spillover to memory-mapped files above a configurable threshold (default 200 MB).

**Rationale:** Common case (1K trials × 10K bars = 80 MB) is comfortably in-memory. `MemoryMappedFile` provides the same `Span<double>` API via `MemoryMappedViewAccessor`, so `SimulationCache` works identically in both modes. `System.IO.MemoryMappedFiles` is in-box — no NuGet needed. Threshold is configurable via existing `IOptions<T>` pattern.

### AD-3: Parallelization Strategy

**Decision:** Stages run sequentially (dependencies on prior results). Within each stage, independent work is parallelized.

| Stage | Parallelism |
|---|---|
| 0–2 | Single-threaded. Sub-second on pre-computed metrics |
| 3 | `Parallel.ForEach` across parameter perturbations |
| 4 | `Parallel.For` across WFO windows |
| 5 | `Parallel.For` across 18 WFM grid cells |
| 6 | `Parallel.For` across MC iterations; `Partitioner+LongRunning` for price permutation re-backtests |
| 7 | `Parallel.For` across C(S,S/2) CSCV combinations |

### AD-4: Pipeline Extensibility

**Decision:** `IValidationStage` interface + explicit ordered stage list in `RunValidationCommandHandler`.

**Rationale:** Stages are tightly defined by the PRD. A simple interface with explicit ordering is more maintainable than a plugin/discovery system. The `ValidationContext.StageData` dictionary allows cross-stage communication without modifying the context class for each new stage.

### AD-5: Equity Curve Storage for Optimization Trials

**Decision:** Modify `OptimizationSetupHelper.ExecuteTrial()` to **store equity curves for trials that pass the filter** (top-N in `BoundedTrialQueue`).

**Rationale:** Storing curves for all 10K+ trials would explode SQLite storage. Storing for only the top-N survivors (typically 50–200) is manageable. For CSCV/PBO which need the full T×N matrix, the `SimulationCacheBuilder` re-derives per-bar returns from stored `TradePnl` (trade-level records already persisted) by replaying trades against the time series — no additional storage needed. Fallback: if equity curves are missing (legacy runs), `SimulationCacheBuilder` re-runs backtests via `BacktestEngine` (slow but functional).

### AD-6: Meta-Overfitting Tracking

**Decision:** `validation_runs` table tracks `invocation_count` per (strategy_name, optimization_run_id). UI displays escalating warnings at counts 3, 5, 10+.

**Rationale:** Directly addresses the Warren Giddings critique from the PRD. Simple, durable (persisted), and purely presentation-layer logic for the warnings.

### AD-7: No New NuGet Packages

**Decision:** All statistical algorithms implemented in-house within Domain.

**Rationale:** The algorithms (DSR, PBO, K-Means, bootstrap, WFO) are well-documented closed-form formulas or straightforward implementations. K-Means for 2–10 dimensions with <1000 points doesn't warrant Accord.NET. Reference implementations exist in Python (`pypbo`, `arch`) for porting. This avoids dependency bloat and keeps Domain dependency-free.

---

## 5. Architecture Diagram

```
                                    ┌─────────────────┐
                                    │   User Request   │
                                    │ POST /validations│
                                    └────────┬────────┘
                                             │
                 ┌───────────────────────────────────────────────────────┐
                 │                     WebApi Layer                      │
                 │  ValidationEndpoints                                  │
                 │  POST /validations  GET /validations/{id}             │
                 │  GET  /validations/{id}/status                        │
                 └───────────────────────┬───────────────────────────────┘
                                         │ RunValidationCommand
                                         ▼
  ┌──────────────────────────────────────────────────────────────────────────┐
  │                          Application Layer                               │
  │                                                                          │
  │  RunValidationCommandHandler                                             │
  │  ├── Load OptimizationRunRecord + Trials from IRunRepository             │
  │  ├── SimulationCacheBuilder ──→ SimulationCache (Domain)                 │
  │  │     └── Equity curves → per-bar P&L deltas → T×N matrix              │
  │  │                                                                       │
  │  ├── Pipeline Orchestrator (sequential gate pattern)                     │
  │  │   ┌──────────────────────────────────────────────────────────┐        │
  │  │   │ Stage 0: Pre-flight (MinBTL, data quality)       instant│        │
  │  │   │ Stage 1: Basic Profitability Filter              instant│        │
  │  │   │ Stage 2: Statistical Significance (DSR, PSR)     instant│        │
  │  │   │ Stage 3: Parameter Landscape (sensitivity, cluster)  med│        │
  │  │   │ Stage 4: Walk-Forward Optimization          ◄── cache   │        │
  │  │   │ Stage 5: Walk-Forward Matrix (6×3 grid)     ◄── cache   │        │
  │  │   │ Stage 6: Monte Carlo & Permutation          ◄── engine  │        │
  │  │   │ Stage 7: Selection Bias Audit (PBO)         ◄── cache   │        │
  │  │   └──────────────────────────────────────────────────────────┘        │
  │  │         each stage: survivors in → filter → survivors out             │
  │  │                                                                       │
  │  ├── CompositeScoreCalculator (Domain) ──→ verdict + score               │
  │  └── Save ValidationRunRecord via IValidationRepository                  │
  │                                                                          │
  │  Progress: RunProgressCache (existing, stage-level granularity)           │
  │  Cancel:   IRunCancellationRegistry (existing)                           │
  └──────────────┬──────────────────────────┬────────────────────────────────┘
                 │                          │
    ┌────────────▼────────────┐  ┌──────────▼──────────────────────┐
    │      Domain Layer       │  │    Infrastructure Layer          │
    │                         │  │                                  │
    │ SimulationCache         │  │ SqliteValidationRepository       │
    │   T×N matrix + slicing  │  │   validation_runs table          │
    │                         │  │   validation_stage_results table  │
    │ Statistics/             │  │                                  │
    │   DeflatedSharpeRatio   │  │ SimulationCacheFileStore         │
    │   PboCalculator         │  │   Memory-mapped binary files     │
    │   MonteCarloBootstrap   │  │   (spillover for large caches)   │
    │   WalkForwardEngine     │  │                                  │
    │   ParameterSensitivity  │  │ SqliteDbInitializer              │
    │   ClusterAnalyzer       │  │   Migration v12+                 │
    │   RegimeDetector        │  │                                  │
    │   SubPeriodAnalyzer     │  └──────────────────────────────────┘
    │   DecayAnalyzer         │
    │                         │
    │ Scoring/                │
    │   CompositeScoreCalc    │
    │                         │
    │ (existing, reused)      │
    │   BacktestEngine        │  ◄── Stage 6 only (price permutation)
    │   MetricsCalculator     │  ◄── Sub-window metric computation
    │   PerformanceMetrics    │  ◄── Extended with recovery factor
    └─────────────────────────┘

  ┌──────────────────────────────────────────────────────────────────────────┐
  │                        Frontend (Next.js)                                │
  │                                                                          │
  │  /validations/[id]  ──  Tabbed layout                                    │
  │  ┌──────────────┬──────────────┬──────────────────────┐                  │
  │  │  Scorecard   │   Stages     │   Charts             │                  │
  │  │              │              │                       │                  │
  │  │ Traffic      │ Expandable   │ Equity Comparison     │                  │
  │  │ Light        │ per-stage    │ Drawdown Underwater   │                  │
  │  │ Verdict      │ pass/fail    │ WFM Heatmap (6×3)    │                  │
  │  │              │ tables with  │ Parameter Surface     │                  │
  │  │ Composite    │ reason codes │ Monte Carlo Fan       │                  │
  │  │ Score 0–100  │              │ PBO Distribution      │                  │
  │  │              │ Drill-down   │ Rolling Sharpe        │                  │
  │  │ Stage        │ to candidate │ Monthly Returns       │                  │
  │  │ Progress Bar │ metrics      │ Heatmap               │                  │
  │  └──────────────┴──────────────┴──────────────────────┘                  │
  └──────────────────────────────────────────────────────────────────────────┘

DATA FLOW:
  optimization_runs (SQLite) ──→ SimulationCacheBuilder ──→ SimulationCache
                                                               │ (in-memory or mmap)
                                                               ▼
  validation_runs (SQLite) ◄── Stages 0–7 read from cache ──→ StageResults
                                (zero re-backtests except Stage 6 permutation)
```

---

## 6. High-Level Implementation Phases

### Phase 1 — Foundation: Simulation Cache + Pipeline Skeleton
**Goal:** Core infrastructure that all subsequent phases depend on.

- `SimulationCache` domain type with T×N matrix and slicing methods + unit tests
- `SimulationCacheBuilder` (load trials → compute deltas → assemble matrix)
- Modify `OptimizationSetupHelper.ExecuteTrial()` to retain equity curves for filtered trials
- `IValidationStage` interface and pipeline orchestrator in `RunValidationCommandHandler`
- `ValidationRunRecord`, `StageResultRecord`, SQLite schema migration v12, `SqliteValidationRepository`
- `RunValidationCommand` + endpoints (`POST`, `GET`, `GET .../status`)
- `ValidationThresholdProfile` with built-in Crypto-Conservative/Standard defaults
- Stub implementations of all 8 stages (pass-through) to validate pipeline wiring
- **Stage 1** (Basic Profitability) and **Stage 2** (Statistical Significance) fully implemented
- `DeflatedSharpeRatio` and PSR calculators in Domain + unit tests

**Key files modified:** `OptimizationSetupHelper.cs` (equity curve storage), `SqliteDbInitializer.cs` (migration v12)

### Phase 2 — Walk-Forward Family + Parameter Analysis
**Goal:** Highest-value validation techniques leveraging the simulation cache.

- `WalkForwardEngine` in Domain — rolling/anchored WFO via cache slicing
- **Stage 4** (Walk-Forward Optimization) fully implemented
- **Stage 5** (Walk-Forward Matrix) — 6×3 grid with contiguous cluster detection
- `ParameterSensitivityAnalyzer` in Domain
- `ClusterAnalyzer` in Domain (K-Means + silhouette scoring)
- **Stage 3** (Parameter Landscape) fully implemented
- Frontend: WFM heatmap, parameter surface visualization

**Why second:** WFO/WFM are 25% of composite score — highest value. They depend on Phase 1's cache.

### Phase 3 — Monte Carlo, Permutation, and Selection Bias
**Goal:** Computationally expensive statistical audits.

- `MonteCarloBootstrap` in Domain (trade bootstrap + price permutation)
- **Stage 6** (Monte Carlo & Permutation) — includes parallel backtest re-runs for permutation tests
- `PboCalculator` in Domain (CSCV with parallelized C(S,S/2) enumeration)
- `RegimeDetector`, `SubPeriodAnalyzer`, `DecayAnalyzer` in Domain
- **Stage 7** (Selection Bias Audit) fully implemented
- Frontend: Monte Carlo fan chart, PBO histogram, rolling Sharpe chart

### Phase 4 — Reporting Dashboard + Composite Scoring
**Goal:** Complete trader-facing interface.

- `CompositeScoreCalculator` — weighted aggregation + hard rejection rules → traffic-light
- Full validation scorecard page (tabbed layout)
- Remaining charts: equity comparison (IS/OOS overlay), drawdown underwater, monthly returns heatmap
- Stage drill-down UI (click stage → candidate verdicts → individual metrics)
- Meta-overfitting warning display (invocation count)
- Side-by-side strategy comparison view

### Phase 5 — Hardening, Export, and Pre-Flight
**Goal:** Production readiness and edge cases.

- `SimulationCacheFileStore` — memory-mapped file backend + automatic spillover
- **Stage 0** (Pre-flight) — MinBTL calculation, data quality validation, cost model validation
- PDF/HTML report export
- Threshold profile management UI (create custom, safety floor enforcement, threshold-shopping detection)
- Integration tests for full pipeline (optimization → validation → results)
- Performance benchmarks and tuning

---

## 7. Verification Plan

1. **Unit tests** for each Domain calculator (DSR, PBO, WFO, Monte Carlo, clustering, regime detection) using known reference values from Python implementations (`pypbo`, `arch`)
2. **Integration test**: optimization → validation pipeline end-to-end with a simple strategy (BuyAndHold) on synthetic data, asserting stage progression and final verdict
3. **Frontend validation**: launch backend + frontend, run optimization via `/optimize`, then trigger validation via UI, verify scorecard renders with all charts
4. **Performance benchmark**: measure simulation cache build time + Stage 5 (WFM) wall-clock for 1000 trials × 10K bars on 4-core machine; target <30 minutes total pipeline
5. **Memory test**: verify automatic spillover to mmap files when cache exceeds threshold; confirm identical results between in-memory and mmap modes
