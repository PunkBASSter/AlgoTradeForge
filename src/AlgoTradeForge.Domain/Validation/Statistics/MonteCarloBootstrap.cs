using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Monte Carlo bootstrap simulation: shuffles bar-level P&amp;L deltas to generate
/// synthetic equity curves, measuring drawdown distribution and probability of ruin.
/// </summary>
public static class MonteCarloBootstrap
{
    private static readonly int[] Percentiles = [5, 25, 50, 75, 95];

    /// <summary>
    /// Runs bootstrap simulation by shuffling P&amp;L deltas across <paramref name="iterations"/> iterations.
    /// Each iteration produces an alternate equity path with the same total P&amp;L but different ordering.
    /// </summary>
    /// <param name="pnlDeltas">Per-bar P&amp;L deltas from the original trial.</param>
    /// <param name="initialEquity">Starting equity for cumulative curve computation.</param>
    /// <param name="iterations">Number of bootstrap iterations (default 1000).</param>
    /// <param name="seed">RNG seed for reproducibility.</param>
    public static MonteCarloResult Run(
        ReadOnlySpan<double> pnlDeltas,
        double initialEquity,
        int iterations,
        int seed = 42)
    {
        if (pnlDeltas.IsEmpty)
        {
            return new MonteCarloResult
            {
                DrawdownPercentiles = Percentiles.ToDictionary(p => p, _ => 0.0),
                EquityFanBands = Array.Empty<double[]>(),
                ProbabilityOfRuin = 0.0,
                Iterations = 0,
            };
        }

        var barCount = pnlDeltas.Length;
        var source = pnlDeltas.ToArray();

        // Pre-allocate per-iteration results
        var maxDrawdowns = new double[iterations];
        var ruinFlags = new int[iterations]; // 1 if equity hit <= 0

        // TODO: Phase 5 hardening — consider streaming percentile computation to avoid
        // full iterations×barCount equity matrix allocation for large bar counts.
        var equityMatrix = new double[iterations][];

        Parallel.For(0, iterations, i =>
        {
            var rng = new Random(seed + i);
            var shuffled = new double[barCount];
            Array.Copy(source, shuffled, barCount);
            StatisticalUtils.FisherYatesShuffle(shuffled, rng);

            var equity = new double[barCount];
            var cumulative = initialEquity;
            var peak = initialEquity;
            var maxDdPct = 0.0;
            var hitRuin = false;

            for (var b = 0; b < barCount; b++)
            {
                cumulative += shuffled[b];
                equity[b] = cumulative;

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
            equityMatrix[i] = equity;
        });

        // Compute drawdown percentiles
        Array.Sort(maxDrawdowns);
        var ddPercentiles = new Dictionary<int, double>(Percentiles.Length);
        foreach (var p in Percentiles)
            ddPercentiles[p] = StatisticalUtils.GetPercentile(maxDrawdowns, p);

        // Compute equity fan bands (5 percentile curves)
        var fanBands = ComputeFanBands(equityMatrix, barCount);

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

    private static double[][] ComputeFanBands(double[][] equityMatrix, int barCount)
    {
        var iterations = equityMatrix.Length;
        var bands = new double[Percentiles.Length][];
        for (var p = 0; p < Percentiles.Length; p++)
            bands[p] = new double[barCount];

        var column = new double[iterations];
        for (var b = 0; b < barCount; b++)
        {
            for (var i = 0; i < iterations; i++)
                column[i] = equityMatrix[i][b];

            Array.Sort(column);

            for (var p = 0; p < Percentiles.Length; p++)
                bands[p][b] = StatisticalUtils.GetPercentile(column, Percentiles[p]);
        }

        return bands;
    }

}
