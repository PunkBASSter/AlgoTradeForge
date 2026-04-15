using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Monte Carlo bootstrap simulation: shuffles trade-level P&amp;L to generate
/// synthetic equity curves, measuring drawdown distribution and probability of ruin.
/// Sequential iteration with array reuse — the outer Parallel.For in the stage
/// already saturates all cores across candidates.
/// </summary>
public static class MonteCarloBootstrap
{
    private static readonly int[] Percentiles = [5, 25, 50, 75, 95];

    /// <summary>
    /// Runs bootstrap simulation by shuffling trade P&amp;L across <paramref name="iterations"/> iterations.
    /// Each iteration produces an alternate equity path with the same total P&amp;L but different ordering.
    /// </summary>
    /// <param name="tradePnls">Per-trade P&amp;L values from the original trial.</param>
    /// <param name="initialEquity">Starting equity for cumulative curve computation.</param>
    /// <param name="iterations">Number of bootstrap iterations (default 1000).</param>
    /// <param name="seed">RNG seed for reproducibility.</param>
    public static MonteCarloResult Run(
        ReadOnlySpan<double> tradePnls,
        double initialEquity,
        int iterations,
        int seed = 42)
    {
        if (tradePnls.IsEmpty)
        {
            return new MonteCarloResult
            {
                DrawdownPercentiles = Percentiles.ToDictionary(p => p, _ => 0.0),
                EquityFanBands = Array.Empty<double[]>(),
                ProbabilityOfRuin = 0.0,
                Iterations = 0,
            };
        }

        var tradeCount = tradePnls.Length;
        var source = tradePnls.ToArray();

        var maxDrawdowns = new double[iterations];
        var ruinFlags = new int[iterations]; // 1 if equity hit <= 0

        // Reusable arrays — sequential iteration avoids per-iteration allocation.
        var rng = new Random(seed);
        var shuffled = new double[tradeCount];
        var equity = new double[tradeCount];

        for (var i = 0; i < iterations; i++)
        {
            Array.Copy(source, shuffled, tradeCount);
            StatisticalUtils.FisherYatesShuffle(shuffled, rng);

            var cumulative = initialEquity;
            var peak = initialEquity;
            var maxDdPct = 0.0;
            var hitRuin = false;

            for (var t = 0; t < tradeCount; t++)
            {
                cumulative += shuffled[t];
                equity[t] = cumulative;

                if (cumulative <= 0)
                    hitRuin = true;

                if (cumulative > peak)
                    peak = cumulative;

                if (peak > 0)
                {
                    var ddPct = (peak - cumulative) / peak * 100.0;
                    if (ddPct > maxDdPct)
                        maxDdPct = ddPct;
                }
            }

            maxDrawdowns[i] = maxDdPct;
            ruinFlags[i] = hitRuin ? 1 : 0;
        }

        // Compute drawdown percentiles
        Array.Sort(maxDrawdowns);
        var ddPercentiles = new Dictionary<int, double>(Percentiles.Length);
        foreach (var p in Percentiles)
            ddPercentiles[p] = StatisticalUtils.GetPercentile(maxDrawdowns, p);

        // Compute equity fan bands (same seeds → identical shuffles)
        var fanBands = ComputeFanBandsSinglePass(source, initialEquity, iterations, seed, tradeCount);

        // Probability of ruin
        var ruinCount = 0;
        for (var i = 0; i < iterations; i++)
            ruinCount += ruinFlags[i];

        return new MonteCarloResult
        {
            DrawdownPercentiles = ddPercentiles,
            EquityFanBands = fanBands,
            ProbabilityOfRuin = (double)ruinCount / iterations,
            Iterations = iterations,
        };
    }

    /// <summary>
    /// Computes per-trade equity fan bands in a single pass. Builds the full equity matrix
    /// (iterations × tradeCount), then extracts column-wise percentiles. O(iterations × tradeCount)
    /// vs the previous two-pass approach which was O(tradeCount² × iterations).
    /// Memory: iterations × tradeCount × 8 bytes (e.g. 2000 × 10K = 160 MB).
    /// </summary>
    private static double[][] ComputeFanBandsSinglePass(
        double[] source, double initialEquity, int iterations, int seed, int tradeCount)
    {
        var bands = new double[Percentiles.Length][];
        for (var p = 0; p < Percentiles.Length; p++)
            bands[p] = new double[tradeCount];

        // Build equity matrix: one row per iteration
        var equityMatrix = new double[iterations][];
        var shuffled = new double[tradeCount];

        for (var i = 0; i < iterations; i++)
        {
            var rng = new Random(seed + i);
            Array.Copy(source, shuffled, tradeCount);
            StatisticalUtils.FisherYatesShuffle(shuffled, rng);

            var row = new double[tradeCount];
            var cumulative = initialEquity;
            for (var t = 0; t < tradeCount; t++)
            {
                cumulative += shuffled[t];
                row[t] = cumulative;
            }

            equityMatrix[i] = row;
        }

        // Column-wise percentiles
        var column = new double[iterations];
        for (var t = 0; t < tradeCount; t++)
        {
            for (var i = 0; i < iterations; i++)
                column[i] = equityMatrix[i][t];

            Array.Sort(column);
            for (var p = 0; p < Percentiles.Length; p++)
                bands[p][t] = StatisticalUtils.GetPercentile(column, Percentiles[p]);
        }

        return bands;
    }
}
