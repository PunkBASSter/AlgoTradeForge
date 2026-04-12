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

- [x] `MetricNormalizer` — piecewise-linear 0–100 normalization for heterogeneous metrics
- [x] `MetricNormalizer` unit tests
- [x] `CompositeScoreResult` record (score, verdict, summary, rejections, category scores)
- [x] `CompositeScoreCalculator` — weighted aggregation (WFO 15%, WFM 10%, Stats 20%, Params 15%, MC 15%, SubPeriod 10%, Data 5%)
- [x] Hard rejection rules → traffic-light verdict (Red / Yellow / Green)
- [x] `CompositeScoreCalculator` unit tests

### Application Layer

- [x] Wired `CompositeScoreCalculator` into `RunValidationCommandHandler`
- [x] Added `CategoryScoresJson`, `RejectionsJson` to `ValidationRunRecord`

### Infrastructure Layer

- [x] SQLite migration v13 — `category_scores_json` and `rejections_json` columns
- [x] `SqliteValidationRepository` — updated INSERT/SELECT/ReadValidationRun for new columns

### WebApi Layer

- [x] `ValidationRunResponse` — added `Rejections` and `CategoryScores` fields
- [x] `ValidationEndpoints` — deserialization of JSON fields in response mapping

### Frontend

- [x] Validation types (`ValidationRun`, `ValidationStatus`, `ValidationSubmission`)
- [x] API client validation methods (get, status, run, cancel, delete)
- [x] `use-validations.ts` hooks (detail, status polling, mutations)
- [x] `verdict-badge.tsx` — Green/Yellow/Red traffic-light badge
- [x] `composite-scorecard.tsx` — score gauge, category bars, rejection alerts, meta-overfitting warning
- [x] `stage-pipeline.tsx` — expandable stage funnel with survival bars
- [x] `candidate-verdicts-table.tsx` — sortable per-stage verdict detail table
- [x] Validation scorecard page (`/report/validation/[id]`) — tabbed layout: Scorecard | Stages | Charts
- [x] Traffic-light verdict + composite score (0–100) + one-sentence summary
- [x] Stage progression bar
- [x] Stage drill-down UI (click stage → candidate verdicts → individual metrics)
- [x] Meta-overfitting warning display (invocation count escalation at 3, 5, 10+)
- [x] `GetValidationEquityQuery` — on-demand reconstruction of PnL deltas from persisted equity curves
- [x] `GET /api/validations/{id}/equity` — serves surviving trials' equity + PnL deltas
- [x] `equity-comparison-chart.tsx` — TradingView multi-line overlay of top survivors
- [x] `drawdown-chart.tsx` — TradingView underwater plot for best survivor
- [x] `monthly-returns-heatmap.tsx` — canvas-based calendar grid (months × years)
- [x] `GET /api/validations` — list endpoint with strategy/profile/date filtering + pagination
- [x] `ListValidationsQuery` + handler + `ValidationRunQuery` filter record
- [x] `SqliteValidationRepository.QueryAsync` — filtered list with WHERE/LIMIT/OFFSET
- [x] `validation-list-table.tsx` — sortable table with checkbox multi-select (up to 4)
- [x] `[strategy]/validation/page.tsx` — validation list page
- [x] `validation-comparison.tsx` — side-by-side category heatmap, score gauges, key metrics
- [x] `/report/validation/compare?ids=...` — comparison page (2–4 runs)
- [x] NavBar — added "Validation" tab

---

## Phase 5 — Hardening, Export, and Pre-Flight

### Critical UX Gap Fix

- [x] `RunValidationDialog` — modal dialog with profile selection for launching validation
- [x] Optimization report page — "Run Validation" button wired to existing `useRunValidation()` hook
- [x] Navigation: Optimization → Run Validation → Validation detail page (auto-redirect)

### Infrastructure Layer

- [x] `SimulationCacheFileStore` — binary file format (write/read/mmap), `MappedSimulationCache` with lazy row loading
- [x] `SimulationCacheFileStore` unit tests (5 tests: round-trip, mmap, dispose, minimal, slice)
- [x] `simulation_cache_metadata` SQLite table (migration v14)
- [x] `threshold_profiles` SQLite table (migration v14)
- [x] `SqliteThresholdProfileRepository` — CRUD for custom profiles
- [x] `ISimulationCacheFileStore` interface (Application) + implementation (Infrastructure)

### Application Layer

- [x] Stage 0: `PreFlightStage` — MinBTL calculation, timestamp gap detection, cost model validation, NaN check
- [x] Stage 0 unit tests (15 tests covering all checks and edge cases)
- [x] `ValidationContext.TotalCombinations` — threaded from handler through pipeline to Stage 0
- [x] `SimulationCacheOptions` — configurable spillover threshold (200 MB default) + cache directory
- [x] `SimulationCacheBuilder.EstimateSize()` — pre-build size estimation for spillover decision
- [x] `RunValidationCommandHandler` — spillover orchestration, custom profile resolution, TotalCombinations
- [x] `ThresholdProfileValidator` — safety floor enforcement (MinTradeCount, MaxPbo, MinWfe)
- [x] `IThresholdProfileRepository` interface
- [x] `ValidationReportGenerator` — standalone HTML report with inline CSS, dark theme, print-friendly

### WebApi Layer

- [x] `GET /api/validations/{id}/report` — HTML report download endpoint
- [x] `GET /api/threshold-profiles` — list all profiles (built-in + custom)
- [x] `POST /api/threshold-profiles` — create custom profile with safety floor validation
- [x] `PUT /api/threshold-profiles/{name}` — update custom profile
- [x] `DELETE /api/threshold-profiles/{name}` — delete custom (reject built-in)

### Frontend

- [x] `run-validation-dialog.tsx` — dynamic profile list from API with fallback
- [x] `threshold-profile.ts` types + `use-threshold-profiles.ts` hook
- [x] API client `getThresholdProfiles()` method
- [x] Validation report page — "Export HTML" button
- [ ] Threshold profile management UI (full editor page — deferred to separate PR)
- [ ] PDF export via browser print (deferred — HTML export covers the use case)

### Testing & Performance

- [x] `ThresholdProfileValidatorTests` — 7 tests (valid profiles, floor violations, custom floors)
- [x] Integration test: full pipeline with 50 profitable trials — composite score computed
- [x] Integration test: all-negative trials — Stage 1 elimination, Red verdict
- [x] Integration test: PreFlight rejection with insufficient data
- [x] Integration test: cancellation, progress callback
- [x] Performance benchmark: 200 trials × 10K bars through full pipeline < 60s
- [x] Memory test: file store write/read round-trip with identical results
