using AlgoTradeForge.Domain.Validation.Results;
using AlgoTradeForge.Domain.Validation.Statistics;

namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Configuration for a single Walk-Forward Optimization run.
/// </summary>
public sealed record WfoConfig
{
    public required int WindowCount { get; init; }
    public required double OosPct { get; init; }
    public required double MinWfe { get; init; }
    public double MinProfitableWindowsPct { get; init; } = 0.70;
    public double MaxOosDrawdownExcess { get; init; } = 0.50;
    public double AnnualizationFactor { get; init; } = 365;
}

/// <summary>
/// Configuration for a Walk-Forward Matrix run (grid of WFOs).
/// </summary>
public sealed record WfmConfig
{
    public required int[] PeriodCounts { get; init; }
    public required double[] OosPcts { get; init; }
    public required double MinWfe { get; init; }
    public int MinContiguousRows { get; init; } = 3;
    public int MinContiguousCols { get; init; } = 3;
    public int MinCellsPassing { get; init; } = 7;
    public double MinProfitableWindowsPct { get; init; } = 0.70;
    public double MaxOosDrawdownExcess { get; init; } = 0.50;
    public double AnnualizationFactor { get; init; } = 365;
}

/// <summary>
/// Executes Walk-Forward Optimization and Walk-Forward Matrix analysis
/// using the simulation cache for zero-re-backtest performance.
/// Windows are defined by timestamp ranges to support variable-length equity curves.
/// </summary>
public static class WalkForwardEngine
{
    /// <summary>
    /// Run a single Walk-Forward Optimization across rolling windows.
    /// Windows are split by timestamp range, supporting per-trial variable-length data.
    /// </summary>
    public static WfoResult RunWfo(SimulationCache cache, WfoConfig config,
        double initialEquity, CancellationToken ct = default)
    {
        var windowCount = config.WindowCount;
        var totalDuration = cache.MaxTimestamp - cache.MinTimestamp;

        if (totalDuration <= 0 || windowCount < 1)
        {
            return new WfoResult
            {
                Windows = [],
                WalkForwardEfficiency = 0,
                ProfitableWindowsPct = 0,
                MaxOosDrawdownExcessPct = 0,
                Passed = false,
            };
        }

        var windowDuration = totalDuration / windowCount;
        if (windowDuration < 1)
        {
            return new WfoResult
            {
                Windows = [],
                WalkForwardEfficiency = 0,
                ProfitableWindowsPct = 0,
                MaxOosDrawdownExcessPct = 0,
                Passed = false,
            };
        }

        var windows = new List<WfoWindowResult>(windowCount);

        for (var w = 0; w < windowCount; w++)
        {
            ct.ThrowIfCancellationRequested();

            var windowStartTs = cache.MinTimestamp + w * windowDuration;
            // long.MaxValue for last window: LowerBound returns array.Length, so FindTrialWindow captures all remaining bars
            var windowEndTs = (w == windowCount - 1)
                ? long.MaxValue
                : cache.MinTimestamp + (w + 1) * windowDuration;

            // IS/OOS split by timestamp
            var isDuration = (long)(windowDuration * (1.0 - config.OosPct));
            if (isDuration < 1) isDuration = 1;
            var oosStartTs = windowStartTs + isDuration;

            if (oosStartTs >= windowEndTs && windowEndTs != long.MaxValue) continue;

            // Pre-compute IS/OOS windows per timeline (avoids N repeated binary searches)
            var isWindows = new (int start, int length)[cache.TimelineCount];
            var oosWindows = new (int start, int length)[cache.TimelineCount];
            for (var tl = 0; tl < cache.TimelineCount; tl++)
            {
                isWindows[tl] = cache.FindTimelineWindow(tl, windowStartTs, oosStartTs);
                oosWindows[tl] = cache.FindTimelineWindow(tl, oosStartTs, windowEndTs);
            }

            // Find best trial on IS data
            var bestTrial = -1;
            var bestFitness = double.MinValue;
            WindowPerformanceMetrics? bestIsMetrics = null;

            for (var t = 0; t < cache.TrialCount; t++)
            {
                var (isStart, isLen) = isWindows[cache.GetTimelineIndex(t)];
                if (isLen < 2) continue;

                var isMetrics = WindowMetricsCalculator.Compute(
                    cache.GetTrialPnlWindow(t, isStart, isLen),
                    initialEquity, config.AnnualizationFactor);
                var fitness = WindowFitnessEvaluator.Evaluate(isMetrics);
                if (fitness > bestFitness)
                {
                    bestFitness = fitness;
                    bestTrial = t;
                    bestIsMetrics = isMetrics;
                }
            }

            if (bestTrial < 0 || bestIsMetrics is null) continue;

            var (bestIsStart, bestIsLen) = isWindows[cache.GetTimelineIndex(bestTrial)];

            // Compute OOS metrics for best trial
            var (oosStart, oosLen) = oosWindows[cache.GetTimelineIndex(bestTrial)];
            if (oosLen < 1) continue;

            var bestOosMetrics = WindowMetricsCalculator.Compute(
                cache.GetTrialPnlWindow(bestTrial, oosStart, oosLen),
                initialEquity, config.AnnualizationFactor);

            // WFE = OOS annualized return / IS annualized return
            var wfe = bestIsMetrics.AnnualizedReturnPct > 0
                ? bestOosMetrics.AnnualizedReturnPct / bestIsMetrics.AnnualizedReturnPct
                : 0;

            windows.Add(new WfoWindowResult
            {
                WindowIndex = w,
                IsStartBar = bestIsStart,
                IsEndBar = bestIsStart + bestIsLen,
                OosStartBar = oosStart,
                OosEndBar = oosStart + oosLen,
                IsMetrics = bestIsMetrics,
                OosMetrics = bestOosMetrics,
                OptimalTrialIndex = bestTrial,
                WalkForwardEfficiency = wfe,
                OosProfitable = bestOosMetrics.TotalReturnPct > 0,
            });
        }

        if (windows.Count == 0)
        {
            return new WfoResult
            {
                Windows = windows,
                WalkForwardEfficiency = 0,
                ProfitableWindowsPct = 0,
                MaxOosDrawdownExcessPct = 0,
                Passed = false,
            };
        }

        var meanWfe = windows.Average(w => w.WalkForwardEfficiency);
        var profitablePct = (double)windows.Count(w => w.OosProfitable) / windows.Count;

        // Max OOS DD excess over IS DD
        var maxDdExcess = 0.0;
        foreach (var w in windows)
        {
            if (w.IsMetrics.MaxDrawdownPct > 0)
            {
                var excess = (w.OosMetrics.MaxDrawdownPct - w.IsMetrics.MaxDrawdownPct)
                    / w.IsMetrics.MaxDrawdownPct;
                if (excess > maxDdExcess)
                    maxDdExcess = excess;
            }
        }

        var passed = meanWfe >= config.MinWfe
            && profitablePct >= config.MinProfitableWindowsPct
            && maxDdExcess <= config.MaxOosDrawdownExcess;

        return new WfoResult
        {
            Windows = windows,
            WalkForwardEfficiency = meanWfe,
            ProfitableWindowsPct = profitablePct,
            MaxOosDrawdownExcessPct = maxDdExcess,
            Passed = passed,
        };
    }

    /// <summary>
    /// Run a Walk-Forward Matrix: grid of WFOs across period counts × OOS percentages.
    /// </summary>
    public static WfmResult RunWfm(SimulationCache cache, WfmConfig config,
        double initialEquity, CancellationToken ct = default)
    {
        var periodCounts = config.PeriodCounts;
        var oosPcts = config.OosPcts;
        var grid = new WfoResult?[periodCounts.Length][];
        for (var pi = 0; pi < periodCounts.Length; pi++)
            grid[pi] = new WfoResult?[oosPcts.Length];

        // Run WFOs in parallel across grid cells
        Parallel.For(0, periodCounts.Length * oosPcts.Length, new ParallelOptions
        {
            CancellationToken = ct,
        }, idx =>
        {
            var pi = idx / oosPcts.Length;
            var oi = idx % oosPcts.Length;

            var wfoConfig = new WfoConfig
            {
                WindowCount = periodCounts[pi],
                OosPct = oosPcts[oi],
                MinWfe = config.MinWfe,
                MinProfitableWindowsPct = config.MinProfitableWindowsPct,
                MaxOosDrawdownExcess = config.MaxOosDrawdownExcess,
                AnnualizationFactor = config.AnnualizationFactor,
            };

            grid[pi][oi] = RunWfo(cache, wfoConfig, initialEquity, ct);
        });

        // Build pass/fail boolean grid
        var passGrid = new bool[periodCounts.Length, oosPcts.Length];
        var passCount = 0;
        for (var pi = 0; pi < periodCounts.Length; pi++)
        {
            for (var oi = 0; oi < oosPcts.Length; oi++)
            {
                passGrid[pi, oi] = grid[pi][oi]?.Passed == true;
                if (passGrid[pi, oi]) passCount++;
            }
        }

        // Find largest contiguous cluster
        var cluster = ContiguousClusterDetector.FindLargestCluster(
            passGrid, config.MinContiguousRows, config.MinContiguousCols, config.MinCellsPassing);

        // Optimal reopt period = center row of cluster
        int? optimalReoptPeriod = null;
        if (cluster is not null)
        {
            var centerRow = cluster.Value.Row + cluster.Value.Rows / 2;
            optimalReoptPeriod = periodCounts[centerRow];
        }

        return new WfmResult
        {
            Grid = grid,
            PeriodCounts = periodCounts,
            OosPcts = oosPcts,
            LargestContiguousCluster = cluster,
            ClusterPassCount = passCount,
            OptimalReoptPeriod = optimalReoptPeriod,
        };
    }
}
