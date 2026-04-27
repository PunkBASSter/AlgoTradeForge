---
description: Run the AlgoTradeForge performance benchmarks (BenchmarkDotNet)
---

## User Input

```text
$ARGUMENTS
```

## Instructions

Invoke the `run-benchmarks` skill. The full guide lives at `.claude/skills/run-benchmarks/SKILL.md` — load it for the canonical instructions on filters, dry-run mode, and how to capture before/after measurements.

### Quick reference

Two scenarios live in `benchmarks/AlgoTradeForge.Benchmarks/`:

- `BacktestBenchmarks.Backtest_5y_Hourly` — single-thread engine throughput over ~43,800 BTCUSDT 1h bars
- `OptimizationBenchmarks.Optimization_1000Trials_Parallel` — 1,000-trial parallel cartesian grid

### Parsing user input

Extract the scenario filter from `$ARGUMENTS`:

| Argument | Action |
|---|---|
| empty / `all` | Run both scenarios at default job |
| `backtest` | `--filter '*Backtest_5y*'` |
| `optimization` / `opt` | `--filter '*Optimization_1000*'` |
| `dry` (anywhere in args) | Append `--job dry` for a single-iteration smoke run |
| explicit BDN flags | Pass through verbatim |

### Run command

From the repo root (`AlgoTradeForge/`):

```bash
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- <filter-and-flags>
```

**Pre-flight check:** make sure no other `dotnet build`, `dotnet test`, `dotnet run` (the WebApi), or active optimization is running. Benchmarks are sensitive to CPU contention — measurements are useless otherwise. Ask the user to confirm the machine is idle if you're unsure.

### After the run

Report the table from `BenchmarkDotNet.Artifacts/results/*.md` (or stdout). Highlight Mean and Allocated changes if a baseline file exists in the working directory or the user supplied one to compare against.
