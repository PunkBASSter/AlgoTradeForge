# Aggregator merge gate (Phase 1b)

P1b-44 of `docs/alternative-bars-tasks.md`.

## Threshold

Phase 1b's `AggregatorBenchmarks` is a merge gate. Block merge if either:

- **Mean** regresses by **>10%** versus the parent commit on any scenario.
- **Allocated** grows by **any** amount on any scenario.

Allocation regressions surface before wall-time ones — a 5% Allocated growth today is often a 15% Mean regression six months later when the data scale catches up.

## Workflow

Capture a baseline on the parent commit, then re-run on the candidate commit and diff via `compare-baseline.ps1`.

```powershell
# On the parent commit (or main pre-PR):
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Filter '*Aggregator*' -Label 'pre-1b'

# Switch to the Phase 1b branch / candidate commit:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Filter '*Aggregator*' -Label 'phase-1b'

# Diff and inspect:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest
```

The compare script prints a paste-ready PR summary. Mean and Allocated columns flag regressions per the thresholds above.

## Scenarios

| Benchmark | Source | Threshold | Notes |
|---|---|---|---|
| `Aggregate_EqV_1h_100k` | BTCUSDT 5y / 1h | 100,000 BTC base | Equal-volume aggregator throughput. |
| `Aggregate_EqT_1h_500` | BTCUSDT 5y / 1h | 500 ticks | Equal-tick (count) aggregator throughput. |

Both use the bundled `benchmarks/AlgoTradeForge.Benchmarks/data/BTCUSDT_1h/` slice (~43,800 source records). Each iteration cleans the output dir so the partition writer always starts fresh.

## Pre-flight

Per `CLAUDE.md`'s "Performance Benchmarks" section:

- Never run benchmarks while another `dotnet` process is active — CPU contention destroys the measurement signal. `save-baseline.ps1` warns when it detects competing PIDs.
- This machine has no `pwsh` (PowerShell 7); always invoke `powershell.exe` (Windows PowerShell 5.1).
