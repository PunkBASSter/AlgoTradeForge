# Overfitting Detection Pipeline — Implementation Tasks

Tracks progress across the 5 phases defined in `overfitting-detection-TRD.md`.

---

## Phase 1 — Foundation: Simulation Cache + Pipeline Skeleton

### Domain Layer

- [x] `SimulationCache` — T×N P&L matrix with `SliceWindow`, `GetTrialPnl`, `GetBarPnl`, `ComputeCumulativeEquity`
- [x] `SimulationCache` unit tests
- [x] `IValidationStage` interface (`StageNumber`, `StageName`, `Execute`)
- [x] `ValidationContext` record (Cache, Trials, Profile, ActiveCandidateIndices)
- [x] `StageResult` and `CandidateVerdict` records
- [x] `TrialSummary` record
- [x] `ValidationThresholdProfile` with Crypto-Conservative / Crypto-Standard presets and safety floors
- [x] `ValidationThresholdProfile` unit tests
- [x] `ProbabilisticSharpeRatio` — DSR and PSR (Bailey & López de Prado 2014)
- [x] `ProbabilisticSharpeRatio` unit tests
- [x] `ReturnSeriesAnalyzer` — log returns + distributional moments (skewness, excess kurtosis)
- [x] `ReturnSeriesAnalyzer` unit tests
- [x] Stage 1: `BasicProfitabilityStage` — min net profit, profit factor, trade count, max drawdown
- [x] `BasicProfitabilityStage` unit tests
- [x] Stage 2: `StatisticalSignificanceStage` — DSR, PSR, Sharpe, profit factor, recovery factor
- [x] `StatisticalSignificanceStage` unit tests
- [x] Stub stages 0, 3–7 (pass-through implementations)
- [x] Stub stage unit tests

### Application Layer

- [x] `SimulationCacheBuilder` — equity curves → per-bar P&L deltas → matrix
- [x] `SimulationCacheBuilder` unit tests
- [x] `ValidationPipeline` — sequential gate orchestrator with progress callback and early exit
- [x] `ValidationPipeline` unit tests (all-survive, early-exit, progress callback, cancellation, candidate flow)
- [x] `RunValidationCommand` + `RunValidationCommandHandler` — background task with 30-min timeout
- [x] `GetValidationByIdQuery` + handler
- [x] `GetValidationStatusQuery` + handler
- [x] `IValidationRepository` interface
- [x] `ValidationRunRecord` + `StageResultRecord` persistence records

### Infrastructure Layer

- [x] `SqliteValidationRepository` — CRUD for validation_runs + validation_stage_results
- [x] `SqliteDbInitializer` — migration adding validation tables
- [x] `SqliteRunRepository` — modified to support equity curve read-back for trials

### WebApi Layer

- [x] `POST /api/validations` — submit validation → 202 Accepted
- [x] `GET /api/validations/{id}` — full results with stage details
- [x] `GET /api/validations/{id}/status` — progress polling
- [x] `POST /api/validations/{id}/cancel` — cancel in-progress run
- [x] `DELETE /api/validations/{id}` — delete with cancel-before-delete safety
- [x] `ValidationContracts` — request/response DTOs

### Existing Code Modifications

- [x] `OptimizationSetupHelper` — retain equity curves for filtered trials (top-N)
- [x] `BacktestRunRecord` — equity curve field support
- [x] `MetricsScaler` — modifications for validation support
- [x] Application `DependencyInjection` — register validation handlers
- [x] Infrastructure `DependencyInjection` — register `SqliteValidationRepository`
- [x] `Program.cs` — map validation endpoints

---

## Phase 2 — Walk-Forward Family + Parameter Analysis

### Domain Layer

- [x] `WalkForwardEngine` — rolling WFO + WFM via SimulationCache slicing, Parallel.For across grid cells
- [x] `WalkForwardEngine` unit tests (window splits, WFE calculation, cancellation, grid dimensions, cluster detection)
- [x] `WfoResult`, `WfoWindowResult`, `WindowPerformanceMetrics` records
- [x] `WfmResult` record (grid of WfoResults + contiguous cluster)
- [x] `WindowMetricsCalculator` — Sharpe, max DD, profit factor from P&L deltas
- [x] `WindowMetricsCalculator` unit tests
- [x] `WindowFitnessEvaluator` — composite fitness adapted for window metrics (no Sortino)
- [x] `ContiguousClusterDetector` — prefix-sum rectangle scan for WFM pass/fail grid
- [x] `ContiguousClusterDetector` unit tests
- [x] `ParameterSensitivityAnalyzer` — neighbor lookup within ±range% + 2D heatmap generation
- [x] `ParameterSensitivityAnalyzer` unit tests
- [x] `ParameterSensitivityResult`, `ParameterHeatmap` records
- [x] `ClusterAnalyzer` — K-Means++ on top-N parameter sets + silhouette scoring
- [x] `ClusterAnalyzer` unit tests
- [x] `ClusterAnalysisResult` record
- [x] `TrialSummary` — added `Parameters` property for Stage 3 access
- [x] `ValidationThresholdProfile` — expanded Stage3/4/5 thresholds, updated CryptoConservative preset

### Application Layer — Stage Implementations

- [x] `SimulationCacheBuilder` — propagates `Parameters` to `TrialSummary`
- [x] Stage 3: `ParameterLandscapeStage` — cluster analysis + sensitivity, NO_PARAMETERS passthrough
- [x] Stage 3 unit tests
- [x] Stage 4: `WalkForwardOptimizationStage` — whole-pool WFO gate (WFE, profitable windows, OOS DD excess)
- [x] Stage 4 unit tests
- [x] Stage 5: `WalkForwardMatrixStage` — 6×3 grid via Parallel.For, contiguous cluster detection
- [x] Stage 5 unit tests

### WebApi Layer

- [x] `StageResultResponse` — added `DetailJson` for rich stage data
- [x] `ValidationEndpoints` — maps `CandidateVerdictsJson` → `DetailJson`

### Frontend

- [x] WFM heatmap component (grid colored by WFE, cluster highlighted with ring)
- [x] Parameter surface component (canvas-based 2D heatmap per parameter pair)
- [x] TypeScript interfaces for WFO/WFM/parameter analysis result types

---

## Phase 3 — Monte Carlo, Permutation, and Selection Bias

### Domain Layer

- [x] `MonteCarloBootstrap` — bar-level bootstrap (shuffle P&L deltas → synthetic equity curves → drawdown percentiles)
- [x] `MonteCarloBootstrap` unit tests
- [x] `MonteCarloResult` record (drawdown percentiles, equity fan bands, probability of ruin)
- [x] `PermutationTester` — P&L delta permutation test (`RunPnlPermutation`)
- [x] `PermutationTester` unit tests
- [x] `PermutationTestResult` record
- [x] `PboCalculator` — CSCV/PBO: partition T×N returns into S blocks, enumerate C(S,S/2) combos
- [x] `PboCalculator` unit tests
- [x] `PboResult` record (PBO, logit distribution, num combinations/blocks)
- [x] `RegimeDetector` — rolling volatility with percentile-based Bull/Bear/Sideways classification
- [x] `RegimeDetector` unit tests
- [x] `RegimeAnalysisResult` record
- [x] `SubPeriodAnalyzer` — equal sub-periods, per-period metrics, R² via linear regression
- [x] `SubPeriodAnalyzer` unit tests
- [x] `SubPeriodConsistencyResult` record
- [x] `DecayAnalyzer` — rolling Sharpe time series + linear regression slope
- [x] `DecayAnalyzer` unit tests
- [x] `DecayAnalysisResult` record
- [x] `ValidationThresholdProfile` — expanded Stage 6/7 thresholds, CryptoConservative overrides

### Stage Implementations (Domain Layer)

- [x] Stage 6: `MonteCarloPnlPermutationStage` — bootstrap drawdown, P&L permutation p-value, cost stress test
- [x] Stage 6 unit tests
- [x] Stage 7: `SelectionBiasAuditStage` — CSCV/PBO gate, sub-period consistency, regime (informational), decay analysis
- [x] Stage 7 unit tests

### Deferred — see `overfitting-monte-carlo-gaps.md`

- [ ] Price permutation test (requires BacktestEngine in ValidationContext)
- [ ] Parameter permutation test (requires BacktestEngine in ValidationContext)
- [ ] Noise injection (requires BacktestEngine in ValidationContext)
- [ ] Hansen's SPA test (optional, overlaps with CSCV/PBO)
- [ ] Trade-level bootstrap (requires ClosedTrade records in ValidationContext)

### Frontend

- [ ] Monte Carlo fan chart (5th/25th/50th/75th/95th percentile bands)
- [ ] PBO distribution histogram
- [ ] Rolling Sharpe chart (with zero-line, decay trend, red highlighting)

---

## Phase 4 — Reporting Dashboard + Composite Scoring

### Domain Layer

- [ ] `CompositeScoreCalculator` — weighted aggregation (WFO 25%, Stats 20%, Params 15%, MC 15%, Regime 10%, SubPeriod 10%, Data 5%)
- [ ] Hard rejection rules → traffic-light verdict (Red / Yellow / Green)
- [ ] `CompositeScoreCalculator` unit tests

### Frontend

- [ ] Validation scorecard page (`/validations/[id]`) — tabbed layout: Scorecard | Stages | Charts
- [ ] Traffic-light verdict + composite score (0–100) + one-sentence summary
- [ ] Stage progression bar
- [ ] Stage drill-down UI (click stage → candidate verdicts → individual metrics)
- [ ] Equity comparison chart (IS, OOS, stitched WFO equity curves overlaid)
- [ ] Drawdown underwater chart
- [ ] Monthly returns heatmap (months × years)
- [ ] Meta-overfitting warning display (invocation count escalation at 3, 5, 10+)
- [ ] Side-by-side strategy comparison view

---

## Phase 5 — Hardening, Export, and Pre-Flight

### Infrastructure Layer

- [ ] `SimulationCacheFileStore` — memory-mapped file backend for caches exceeding threshold (200 MB)
- [ ] `SimulationCacheFileStore` unit tests
- [ ] `simulation_cache_metadata` SQLite table (optimization_run_id, bar_count, trial_count, cache_file_path)

### Application Layer

- [ ] Stage 0: `PreFlightStage` — full implementation (MinBTL calculation, data quality, cost model validation)
- [ ] Stage 0 unit tests

### Frontend

- [ ] Threshold profile management UI (create custom profiles, safety floor enforcement)
- [ ] Threshold-shopping detection / warning
- [ ] PDF/HTML report export

### Testing & Performance

- [ ] Integration test: optimization → validation pipeline end-to-end (BuyAndHold on synthetic data)
- [ ] Performance benchmark: SimulationCache build + WFM wall-clock for 1000 trials × 10K bars
- [ ] Memory test: automatic spillover to mmap, verify identical results in-memory vs mmap
