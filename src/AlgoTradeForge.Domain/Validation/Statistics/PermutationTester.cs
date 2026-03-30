using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Permutation test for strategy performance significance. Shuffles the return sequence
/// to test whether the observed metric (Sharpe) depends on sequential ordering.
/// Extensible: <c>RunPnlPermutation</c> now; future <c>RunPricePermutation</c> and
/// <c>RunParameterPermutation</c> can be added when BacktestEngine access is available.
/// </summary>
public static class PermutationTester
{
    /// <summary>
    /// Tests whether the observed Sharpe ratio depends on the sequential ordering of P&amp;L deltas.
    /// Shuffles the deltas <paramref name="iterations"/> times, computing Sharpe for each permutation.
    /// P-value = fraction of permuted Sharpes ≥ observed Sharpe.
    /// </summary>
    /// <param name="pnlDeltas">Per-bar P&amp;L deltas from the original trial.</param>
    /// <param name="initialEquity">Starting equity (used for return computation).</param>
    /// <param name="iterations">Number of permutation iterations.</param>
    /// <param name="annualizationFactor">Bars per year for Sharpe annualization (default 365).</param>
    /// <param name="seed">RNG seed for reproducibility.</param>
    public static PermutationTestResult RunPnlPermutation(
        ReadOnlySpan<double> pnlDeltas,
        double initialEquity,
        int iterations,
        double annualizationFactor = 365,
        int seed = 42)
    {
        if (pnlDeltas.Length < 2)
        {
            return new PermutationTestResult
            {
                PValue = 1.0,
                OriginalMetric = 0.0,
                PermutedDistribution = [],
                Iterations = 0,
                TestType = "PnlDelta",
            };
        }

        var source = pnlDeltas.ToArray();
        var observedSharpe = ComputeSharpe(source, initialEquity, annualizationFactor);

        var permutedSharpes = new double[iterations];
        var exceedCount = new int[iterations]; // 1 if permuted >= observed

        Parallel.For(0, iterations, i =>
        {
            var rng = new Random(seed + i);
            var shuffled = new double[source.Length];
            Array.Copy(source, shuffled, source.Length);
            StatisticalUtils.FisherYatesShuffle(shuffled, rng);

            var permSharpe = ComputeSharpe(shuffled, initialEquity, annualizationFactor);
            permutedSharpes[i] = permSharpe;
            exceedCount[i] = permSharpe >= observedSharpe ? 1 : 0;
        });

        var totalExceed = 0;
        for (var i = 0; i < iterations; i++)
            totalExceed += exceedCount[i];

        return new PermutationTestResult
        {
            PValue = (double)totalExceed / iterations,
            OriginalMetric = observedSharpe,
            PermutedDistribution = permutedSharpes,
            Iterations = iterations,
            TestType = "PnlDelta",
        };
    }

    /// <summary>
    /// Computes annualized Sharpe ratio from P&amp;L deltas. Returns are computed as
    /// pnlDelta[i] / equity[i-1] to capture proportional returns.
    /// </summary>
    internal static double ComputeSharpe(double[] pnlDeltas, double initialEquity, double annualizationFactor)
    {
        if (pnlDeltas.Length < 2) return 0.0;

        var n = pnlDeltas.Length;
        var sumReturn = 0.0;
        var sumReturnSq = 0.0;
        var equity = initialEquity;

        for (var i = 0; i < n; i++)
        {
            var ret = equity > 0 ? pnlDeltas[i] / equity : 0.0;
            sumReturn += ret;
            sumReturnSq += ret * ret;
            equity += pnlDeltas[i];
        }

        var meanReturn = sumReturn / n;
        var variance = sumReturnSq / n - meanReturn * meanReturn;
        if (variance <= 0) return 0.0;

        var stdev = Math.Sqrt(variance);
        return (meanReturn / stdev) * Math.Sqrt(annualizationFactor);
    }

}
