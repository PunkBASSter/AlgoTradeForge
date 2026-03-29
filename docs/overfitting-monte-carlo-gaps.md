# Deferred Monte Carlo & Permutation Features

Tracks features from the overfitting detection PRD (`overfitting-detection-requirements.md`) and TRD (`overfitting-detection-TRD.md`) that were deferred during Phase 3 implementation. Each gap includes product requirements, technical design, and implementation tasks.

---

## What's Implemented vs. Deferred

### Implemented (Phase 3, Stage 6: `MonteCarloPnlPermutationStage`)

| Technique | What it tests | Data source |
|-----------|--------------|-------------|
| Bar-level P&L bootstrap | Path dependency — is the observed drawdown typical or lucky? | `SimulationCache` P&L deltas |
| P&L delta permutation | Sequential significance — does the return ordering matter? | `SimulationCache` P&L deltas |
| Transaction cost stress | Thin-edge strategies — profitable at 2× costs? | `PerformanceMetrics` aggregates |

### Deferred

| Technique | What it tests | Why deferred |
|-----------|--------------|-------------|
| Price permutation | Curve-fitting to specific price patterns | Requires re-backtesting on synthetic price data → BacktestEngine access |
| Parameter permutation | Parameter fragility near the optimal | Requires re-backtesting with varied parameters → BacktestEngine access |
| Noise injection | Robustness to data imprecision | Requires re-backtesting on noisy OHLC → BacktestEngine access |
| Hansen's SPA test | Selection bias vs. benchmark | High compute cost, overlaps with CSCV/PBO |
| Trade-level bootstrap | True trade-path dependency | Requires individual trade records in ValidationContext |

### Common Blocker

All deferred techniques except Hansen's SPA share the same prerequisite: **`BacktestEngine` must be accessible from within the validation pipeline**. Currently, `ValidationContext` carries only `SimulationCache`, `TrialSummary[]`, and `ValidationThresholdProfile` — all pure domain types with no engine reference. Adding BacktestEngine access requires:

1. Extending `ValidationContext` with an engine reference or callback
2. Providing the original `TimeSeries<Int64Bar>` price data to the validation pipeline
3. Providing `IInt64BarStrategy` factory so strategies can be re-instantiated with new parameters

This is an architectural change that impacts the clean separation between domain-level validation (pure functions over cached data) and application-level orchestration (engine access, I/O). The recommended approach is to make the engine-dependent permutation tests **application-layer stage implementations** that receive the engine via constructor injection, rather than domain-level pure functions.

---

## Gap 1: Price Permutation Test

### Product Requirements (from PRD §2.4)

> **Price permutation tests** (Timothy Masters method) transform prices to log-returns, randomly permute them, exponentiate back to synthetic price series preserving statistical properties but destroying temporal patterns, then re-run the strategy on 1,000+ permuted series. If the original performance exceeds ≥95% of permuted results (p < 0.05), the strategy has a genuine edge.

**Failure mode caught:** Curve-fitting to specific price patterns that are noise. This is distinct from P&L permutation — price permutation tests whether the strategy's *signal generation* is genuine, while P&L permutation tests whether the *return ordering* matters.

**Thresholds:** p-value < 0.05 (Crypto-Standard), < 0.01 (Crypto-Conservative).

### Technical Design

**Algorithm:**
1. Load the original OHLC price series used for the optimization.
2. Compute log-returns: `lr[i] = ln(close[i] / close[i-1])`.
3. For each of 1,000 iterations:
   a. Shuffle the log-returns (Fisher-Yates).
   b. Reconstruct a synthetic price series: `synth[i] = synth[i-1] × exp(lr_shuffled[i])`.
   c. Re-run the full backtest via `BacktestEngine.Run()` on the synthetic series.
   d. Compute the performance metric (Sharpe) for this run.
4. p-value = fraction of permuted Sharpes ≥ observed Sharpe.

**Key difference from P&L permutation:** Price permutation re-runs the strategy's *signal logic* on different price patterns. If the strategy generates signals from, e.g., moving average crossovers on specific price formations, price permutation tests whether those formations are genuine or noise. P&L permutation only tests whether the observed return sequence's ordering is significant, without re-evaluating the strategy's entry/exit decisions.

**Prerequisites:**
- `BacktestEngine` reference in `ValidationContext` or stage constructor
- Original `TimeSeries<Int64Bar>` price data
- `IInt64BarStrategy` factory to re-instantiate the strategy
- `BacktestOptions` from the original optimization run

**Computational cost:** 1,000 × full backtest ≈ 10–60 minutes depending on strategy complexity.

**Parallelization:** `Parallel.For` across iterations. Each iteration is independent (own synthetic price series + backtest run). Use `Partitioner` with `LongRunning` for CPU-bound backtest work.

### Implementation Tasks

- [ ] Extend `ValidationContext` or create `EngineAwareValidationContext` with `BacktestEngine`, `TimeSeries<Int64Bar>`, `IInt64BarStrategy` factory
- [ ] Implement `PricePermutationRunner` in Application layer (not Domain — requires engine I/O)
- [ ] Add `RunPricePermutation()` method or separate class
- [ ] Add `NoiseIter`, `MinPermutationPValue` to Stage 6 thresholds (or create separate stage)
- [ ] Unit tests with mock `BacktestEngine`
- [ ] Integration test: strategy on synthetic data → price permutation → verify p-value

---

## Gap 2: Parameter Permutation Test

### Product Requirements (from PRD §2.4)

> **Parameter sensitivity Monte Carlo** randomizes parameters near optimal values (±10–20%) across 1,000+ iterations and re-backtests. If performance degrades sharply, the optimal is a fragile peak rather than a robust plateau.

**Failure mode caught:** Parameter fragility — whether the optimal is a robust plateau or an isolated spike. This complements Stage 3's `ParameterSensitivityAnalyzer` (which uses neighbor lookup within existing optimization results) by actually re-running backtests with perturbed parameters, covering parameter combinations not in the original optimization grid.

**Note:** Stage 3 already implements parameter sensitivity analysis via cached optimization results. Parameter permutation with re-backtesting adds value when the optimization grid is coarse (large step sizes) and neighbors in the cached results are far apart. For fine-grained grids, Stage 3's approach is sufficient.

**Thresholds:** Mean Sharpe retention ≥ 60% of optimal (Crypto-Standard), ≥ 70% (Crypto-Conservative).

### Technical Design

**Algorithm:**
1. Take the optimal parameter combination from the candidate.
2. For each of 1,000 iterations:
   a. Perturb each parameter by a random factor in [−range%, +range%] (e.g., ±10%).
   b. Re-run the backtest with the perturbed parameters via `BacktestEngine.Run()`.
   c. Record the resulting Sharpe ratio.
3. Compute mean Sharpe retention = mean(permuted Sharpes) / observed Sharpe.
4. Reject if retention < threshold.

**Prerequisites:**
- Same as Gap 1 (BacktestEngine + strategy factory)
- Parameter combination from `TrialSummary.Parameters`
- `ParameterCombination` builder to apply perturbations

**Computational cost:** 1,000 × full backtest.

### Implementation Tasks

- [ ] Implement `ParameterPermutationRunner` in Application layer
- [ ] Add threshold: `MinParameterRetention` to Stage 6 thresholds
- [ ] Unit tests with mock engine
- [ ] Integration test

---

## Gap 3: Noise Injection

### Product Requirements (from PRD §2.6)

> **Noise injection** adds Gaussian noise scaled to ±5–10% of the daily bar range to OHLC data and re-runs the strategy on 1,000+ synthetic series. The strategy should retain >70% of its Sharpe ratio.

**Failure mode caught:** Data precision dependency — whether the strategy exploits exact price levels that wouldn't be reproducible with real-world data quality variations (exchange differences, different data providers, slight timing discrepancies).

**Thresholds:** Sharpe retention ≥ 70% (Crypto-Standard), ≥ 80% (Crypto-Conservative).

### Technical Design

**Algorithm:**
1. Load the original OHLC data.
2. For each of 500 iterations:
   a. For each bar, add Gaussian noise scaled to `noisePct × (high - low)` to each of O, H, L, C.
   b. Ensure OHLC validity: H = max(O,H,C,L), L = min(O,H,C,L).
   c. Re-run the backtest on the noisy data.
   d. Record the resulting Sharpe.
3. Mean Sharpe retention = mean(noisy Sharpes) / observed Sharpe.

**Prerequisites:**
- Same as Gap 1 (BacktestEngine + price data)
- `noisePct` parameter (default 0.05–0.10)

**Computational cost:** 500 × full backtest.

### Implementation Tasks

- [ ] Implement `NoiseInjectionRunner` in Application layer
- [ ] OHLC noise generator with validity enforcement
- [ ] Add thresholds: `NoiseIterations`, `MinNoiseRetention`, `NoisePct` to Stage 6
- [ ] Unit tests
- [ ] Integration test

---

## Gap 4: Hansen's SPA Test

### Product Requirements (from PRD §2.5)

> **Hansen's SPA Test** tests whether the best strategy from the entire tested universe genuinely beats a benchmark (buy-and-hold) after accounting for data-snooping from testing many alternatives. Uses stationary block bootstrap with 1,000+ replications.

**Failure mode caught:** Selection bias from testing many strategies — even if the best strategy looks good, it might just be the best of many noise-fitted alternatives.

**Overlap with CSCV/PBO:** Both test selection bias. CSCV/PBO measures whether IS-optimal strategies persist OOS. Hansen's SPA measures whether the best strategy beats a specific benchmark after multiple-testing correction. They are complementary but the marginal value of SPA is lower when PBO is already computed.

**Priority:** Low. Recommended to implement only if institutional-grade statistical rigor is needed for external reporting.

**Thresholds:** SPA p-value < 0.05.

### Technical Design

**Algorithm:**
1. Compute excess returns for each trial vs. buy-and-hold benchmark.
2. For each of 1,000 replications:
   a. Generate stationary block bootstrap of the excess return series (block length ≈ √T).
   b. Compute the test statistic for each trial.
3. Compute the studentized SPA statistic.
4. p-value from the bootstrap distribution.

**Prerequisites:**
- Buy-and-hold benchmark return series (can be derived from price data)
- Full T×N return matrix (available in `SimulationCache`)
- Stationary block bootstrap implementation

**Computational cost:** 1,000 × matrix operations ≈ seconds to minutes (no re-backtesting needed).

### Implementation Tasks

- [ ] Implement `StationaryBlockBootstrap` utility in Domain
- [ ] Implement `SpaCalculator` in Domain (pure function over SimulationCache)
- [ ] Add thresholds: `SpaReplications`, `MinSpaPValue` to Stage 7
- [ ] Provide benchmark return series (buy-and-hold) to ValidationContext
- [ ] Unit tests with known reference values from Python `arch` library

---

## Gap 5: Trade-Level Bootstrap

### Current State

The implemented bootstrap (`MonteCarloBootstrap`) shuffles **bar-level P&L deltas** — each bar is treated as an independent unit. This is a valid and common approach for testing path dependency.

### What Trade-Level Bootstrap Would Add

True trade-level bootstrap shuffles **individual completed trades** (entry→exit sequences spanning multiple bars), preserving the internal structure of each trade but randomizing their ordering. This is more realistic because:

1. **Trade duration matters:** A bar-level shuffle can break apart multi-bar trades, creating impossible equity paths.
2. **Trade clustering:** Real strategies often have clusters of correlated trades (e.g., multiple positions in trending markets). Bar-level shuffle destroys these clusters.
3. **Drawdown accuracy:** Trade-level drawdown estimates are more accurate because they respect the atomic unit of risk.

### Tradeoffs

| Aspect | Bar-level (current) | Trade-level (deferred) |
|--------|---------------------|----------------------|
| Data requirement | P&L deltas (in SimulationCache) | Individual trade records (not in ValidationContext) |
| Granularity | Fine (per-bar) | Coarse (per-trade) |
| Trade structure | Broken by shuffle | Preserved |
| Implementation | Simple | Requires `ClosedTrade` records |
| Accuracy | Conservative (more shuffle freedom → wider DD distribution) | More realistic |

### Prerequisites

- `ClosedTrade` or equivalent trade record type with: entry timestamp, exit timestamp, P&L, duration
- Trade records stored per trial in optimization results (currently only aggregate `PerformanceMetrics` is stored)
- Extension of `SimulationCache` or `ValidationContext` to carry `ClosedTrade[][]`

### Implementation Tasks

- [ ] Define `ClosedTrade` record in Domain (or reuse `Fill` aggregation)
- [ ] Store per-trial trade records during optimization (modify `OptimizationSetupHelper`)
- [ ] Add `TrialTrades: ClosedTrade[][]` to `SimulationCache` or `ValidationContext`
- [ ] Implement `TradeBootstrap` calculator in Domain
- [ ] Unit tests
- [ ] Block bootstrap variant for overlapping/concurrent trades (PRD §10 concern)

---

## Common Prerequisites Summary

| Prerequisite | Needed by | Effort |
|-------------|-----------|--------|
| `BacktestEngine` in validation pipeline | Price perm, Param perm, Noise injection | Medium — architectural change to `ValidationContext` or stage DI |
| Original `TimeSeries<Int64Bar>` price data | Price perm, Noise injection | Low — pass through from `RunValidationCommandHandler` |
| `IInt64BarStrategy` factory | Price perm, Param perm, Noise injection | Low — pass `IStrategyFactory` through |
| `BacktestOptions` from original run | All engine-dependent | Low — already stored in `OptimizationRunRecord` |
| `ClosedTrade[][]` trade records | Trade-level bootstrap | Medium — requires storage extension |
| Buy-and-hold benchmark returns | Hansen's SPA | Low — derive from price data |

## Recommended Implementation Order

1. **Hansen's SPA** — no BacktestEngine needed, works from existing `SimulationCache`
2. **Common prerequisite:** BacktestEngine access in validation pipeline
3. **Price permutation** — highest value among engine-dependent tests
4. **Noise injection** — complements price permutation
5. **Parameter permutation** — overlaps with Stage 3 sensitivity analysis
6. **Trade-level bootstrap** — requires separate storage extension
