# AlgoTradeForge.Benchmarks

Performance benchmarks for the backtest engine and optimization loop.

## Run

```bash
# Everything
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks

# One scenario (filter is glob over `Class.Method`)
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*Backtest_5y*'
dotnet run -c Release --project benchmarks/AlgoTradeForge.Benchmarks -- --filter '*Optimization_1000*'
```

Results land under `BenchmarkDotNet.Artifacts/` next to the working directory at run time.

## What's measured

- **`BacktestBenchmarks.Backtest_5y_Hourly`** — one `BacktestEngine.Run` over ~43,800 BTCUSDT 1h bars (2020-01 → 2024-12) with `PrevBarBreakoutStrategy`. Single-threaded; isolates engine + strategy throughput.
- **`OptimizationBenchmarks.Optimization_1000Trials_Parallel`** — 1,000-combination cartesian grid (`EntryOffsetTicks × SlBufferTicks × MaxBars`), partitioned across `Environment.ProcessorCount` workers. Mirrors the per-DSS loop shape from `OptimizationTaskExecutor.cs`.

Both scenarios load CSV data once in `[GlobalSetup]` so disk I/O is excluded from measurements. `[MemoryDiagnoser]` reports allocations — watch this for per-bar object churn regressions.

## Sample strategy

`PrevBarBreakoutStrategy` lives in the Domain (`src/AlgoTradeForge.Domain/Strategy/PrevBarBreakout/`) and is reused by the benchmark. It places a Buy-stop above the previous bar's High and a Sell-stop below the previous bar's Low on every bar; any active position is market-closed at the next bar's close, and any unfilled pending order is cancelled the same bar. The bundled ATR indicator powers an optional `MinVolatilityPct` entry filter (disabled = 0; e.g. 0.5 means "ATR ≥ 0.5 % of prev close"). The benchmark runs with the filter off so registry/engine hot paths see full order flow.

## Capturing baselines

The harness emits `*-report-brief.json` (via `[Config(typeof(BriefJsonConfig))]`) alongside markdown so a small diff script can do the math.

```bash
# On the parent commit:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Label 'pre-fix'

# On the new commit:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Label 'post-fix'

# Diff:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest
```

Snapshots land in `~/.algo-tradeforge/perf-history/<sha>[-dirty]-<utc>[-<label>]/` (per-machine; never committed — `BenchmarkDotNet.Artifacts/` is also gitignored). `compare-baseline.ps1` accepts literal paths, snapshot names, or the aliases `latest` / `previous`; it warns if the two snapshots came from different machines, filter sets, or job kinds, and prints a paste-ready PR summary.

The harness itself isn't on CI yet — run it locally on the same machine for both before/after to keep results comparable. This machine doesn't have `pwsh` (PowerShell 7+) installed, so always invoke `powershell.exe` (Windows PowerShell 5.1) explicitly.

## Bundled data

`data/BTCUSDT_1h/*.csv` is a verbatim copy of the local AlgoTradeForge history catalog (`%LOCALAPPDATA%\AlgoTradeForge\History\binance\BTCUSDT\candles\`) for 2020-01 → 2024-12. ~2.4 MB total. Refresh by re-copying if the upstream catalog format changes.
