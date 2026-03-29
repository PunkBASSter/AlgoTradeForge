Validation techniques:

CPCV — Combinatorially Purged Cross-Validation. Generates all possible fold assignments from data groups, with purging (removing training observations whose label windows overlap test periods) and embargoing (buffering post-test observations). Catches temporal information leakage.
V-in-V — Validation-within-Validation (from Timothy Masters). Three-partition lifecycle: 60% outer training, 20% inner validation, 20% final test. Catches researcher degrees of freedom.
CSCV — Combinatorially Symmetric Cross-Validation. Divides the performance matrix into S equal blocks, generates all C(S, S/2) IS/OOS splits, and checks whether IS-optimal strategies rank below OOS median. Produces PBO.
PBO — Probability of Backtest Overfitting. The output metric from CSCV — the proportion of combinatorial splits where the in-sample best underperforms out-of-sample. Lower is better (< 0.30 for crypto).
WFO — Walk-Forward Optimization. Pardo's method: optimize on IS window, test on the next OOS window, roll forward, repeat, stitch all OOS segments into one equity curve.
WFE — Walk-Forward Efficiency. Annualized OOS return ÷ annualized IS return. Target ≥ 50%, prefer ≥ 60%.
WFM — Walk-Forward Matrix. Runs multiple WFOs across a grid of IS/OOS window configurations (e.g., 6 period counts × 3 OOS percentages = 18 WFOs). Catches sensitivity to temporal slicing choices.
DSR — Deflated Sharpe Ratio. Bailey & López de Prado's correction of the Sharpe ratio for selection bias (number of trials tested), non-normality, and track record length.
PSR — Probabilistic Sharpe Ratio. Single-trial version of DSR — the probability that the true Sharpe exceeds a benchmark, accounting for sample length, skewness, and kurtosis.
MinBTL — Minimum Backtest Length. Formula-based check of whether enough data exists to support the planned number of parameter configurations.
SPA — Superior Predictive Ability (Hansen's test). Tests whether the best strategy genuinely beats a benchmark after accounting for data-snooping.
RC — Reality Check (White's). Predecessor to Hansen's SPA, same goal but more conservative.
StepM — Stepwise Multiple Testing (Romano-Wolf). Extension of White's RC that identifies the entire set of genuinely superior strategies, not just the single best.
ONC — Optimal Number of Clusters. López de Prado's algorithm for estimating the effective number of independent trials — an input to DSR.
HMM — Hidden Markov Model. Statistical model used for regime detection (bull/bear/sideways states).
PSO — Particle Swarm Optimization. Search algorithm referenced in the Plateau Score framework for efficient high-dimensional parameter landscape analysis.

Data partitioning terms:

IS — In-Sample. The training portion of data used for optimization.
OOS — Out-of-Sample. The holdout portion reserved for testing.

General metrics/statistical:

SR — Sharpe Ratio.
CLT — Central Limit Theorem. Justification for the 30-trade minimum floor.
CI — Confidence Interval.
R² — Coefficient of determination. Used here for equity curve smoothness (linear regression fit).
OHLCV — Open, High, Low, Close, Volume. Standard bar data fields.
P&L — Profit and Loss.
DBSCAN — Density-Based Spatial Clustering of Applications with Noise. Clustering algorithm used for parameter set analysis.

Platform/external references:

AFML — Advances in Financial Machine Learning (López de Prado's book/framework).
GARCH — Generalized Autoregressive Conditional Heteroskedasticity. A volatility model mentioned in the context of synthetic data generation.
LRU — Least Recently Used. Eviction policy for the disk-backed simulation cache.