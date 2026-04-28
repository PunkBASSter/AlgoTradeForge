---
name: run-benchmarks
description: Run the AlgoTradeForge backtest + optimization performance benchmarks (BenchmarkDotNet harness in benchmarks/AlgoTradeForge.Benchmarks). Use to capture before/after measurements when changing engine, strategy, indicator, optimization, or trade-registry hot paths.
user-invocable: true
---

# Run Performance Benchmarks

Drives the BenchmarkDotNet suite at `benchmarks/AlgoTradeForge.Benchmarks/`. Use this whenever a change touches code that runs per-bar or per-trial — it's the only way to attribute a slowdown (or speedup) to a specific commit instead of guessing from production-scale optimization runs.

## When to use

- Before merging any change to `BacktestEngine`, `ModularStrategyBase`, `StrategyBase`, `TradeRegistryModule`, indicator hot paths, or `OptimizationTaskExecutor`
- When investigating a perceived perf regression (capture baseline on the old commit, then diff against the new one)
- After a refactor that touches per-bar allocation patterns — the `[MemoryDiagnoser]` numbers will catch churn that wall-time alone would miss

## Scenarios

| Benchmark | What it measures |
|---|---|
| `BacktestBenchmarks.Backtest_5y_Hourly` | Single-thread throughput of `BacktestEngine.Run` over ~43,800 BTCUSDT 1h bars (2020-01 → 2024-12) using `PrevBarBreakoutStrategy` (exercises TradeRegistry + FixedNotional + MaxHoldBars). |
| `OptimizationBenchmarks.Optimization_1000Trials_Parallel` | 1,000-combination cartesian grid partitioned across `Environment.ProcessorCount` workers, mirroring `OptimizationTaskExecutor.cs:111-214`'s loop shape. |

Both load CSV data once in `[GlobalSetup]` so disk I/O is excluded from the timed region.

## How to run

Run from the repo root (`AlgoTradeForge/`):

```bash
# Smoke-run a single iteration (validates wiring; ~1s + 40s respectively)
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*Backtest_5y*' --job dry
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*Optimization_1000*' --job dry

# Full measurement run (default job: ~12 iterations + warmup, ~few minutes per scenario)
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*Backtest_5y*'
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*Optimization_1000*'

# All benchmarks
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks
```

**Critical:** Do **not** run benchmarks while another `dotnet build`, `dotnet test`, or backend server is running on the same machine — concurrent CPU contention destroys the measurement signal.

Build separately first if you want to keep the run clean:

```bash
dotnet build -c Release benchmarks/AlgoTradeForge.Benchmarks/AlgoTradeForge.Benchmarks.csproj
dotnet run --no-build -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*'
```

## Reading the output

Results land in `BenchmarkDotNet.Artifacts/results/` next to the working directory (CSV, GitHub-flavored markdown, HTML). The columns that matter:

- **Mean** — wall-time per invocation. The headline number.
- **Allocated** — managed bytes per invocation. Per-bar allocation regressions show up here long before they show up in wall time.
- **Gen0/Gen1/Gen2** — collections per 1000 ops. A spike in Gen2 indicates large or long-lived allocations (suspect: registry/strategy state growing across the run).

## Capturing before/after measurements

Use the helper scripts at `scripts/perf/` — they archive the brief JSON outputs (emitted via `[Config(typeof(BriefJsonConfig))]`) and diff them. Snapshots are written to `~/.algo-tradeforge/perf-history/<sha>[-dirty]-<utc>[-<label>]/` (per-machine, outside the repo).

```bash
# Parent commit:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Label 'pre-fix'

# New commit:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Label 'post-fix'

# Diff (alphabetical by mtime: 'previous' = baseline, 'latest' = candidate):
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest
```

`save-baseline.ps1` accepts `-Filter '<glob>'` (default `*` = both scenarios) and `-Job dry|default`. `compare-baseline.ps1` resolves snapshots as a literal path, a name under `~/.algo-tradeforge/perf-history/`, or the aliases `latest` / `previous`; it warns on machine/filter/job mismatches and prints a paste-ready PR summary. Both **Mean** and **Allocated** matter — a 5 % wall-time drop with a 50 % allocation drop is still a real win (less GC pressure under sustained load).

Note: this machine doesn't have `pwsh` (PowerShell 7+) installed, so always invoke `powershell.exe` (Windows PowerShell 5.1) explicitly.

## Strategy & data details

The benchmark uses `PrevBarBreakoutStrategy` (`src/AlgoTradeForge.Domain/Strategy/PrevBarBreakout/`): a symmetric reference strategy that places a Buy-stop above the previous bar's high and a Sell-stop below the previous bar's low on every bar, market-closes any active position at the next bar's close, and cancels any unfilled pendings the same bar. An optional ATR-based volatility filter (`MinVolatilityPct`) can gate entries on `(ATR / prev.Close) × 100` being above a threshold; the benchmark runs with the filter disabled (`0.0`) to exercise the happy path. The strategy is intentionally heavy on order traffic so registry and engine hot paths are stressed.

Bundled data (`benchmarks/AlgoTradeForge.Benchmarks/data/BTCUSDT_1h/`) is a verbatim copy of the local AlgoTradeForge history catalog for 2020-01 → 2024-12 (60 monthly CSVs, ~2.4 MB). Refresh by re-copying from `%LOCALAPPDATA%\AlgoTradeForge\History\binance\BTCUSDT\candles\` if the upstream format ever changes.

See `benchmarks/AlgoTradeForge.Benchmarks/README.md` for additional notes.
