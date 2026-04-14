using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Permutation test for strategy performance significance. Shuffles the trade P&amp;L
/// to test whether the observed metric (Sharpe) depends on sequential ordering.
/// Sequential iteration with array reuse — the outer Parallel.For in the stage
/// already saturates all cores across candidates.
/// </summary>
public static class PermutationTester
{
    /// <summary>
    /// Tests whether the observed Sharpe ratio depends on the sequential ordering of trade P&amp;L.
    /// Shuffles the P&amp;L values <paramref name="iterations"/> times, computing Sharpe for each permutation.
    /// P-value = fraction of permuted Sharpes ≥ observed Sharpe.
    /// </summary>
    /// <param name="tradePnls">Per-trade P&amp;L values from the original trial.</param>
    /// <param name="initialEquity">Starting equity (used for return computation).</param>
    /// <param name="iterations">Number of permutation iterations.</param>
    /// <param name="annualizationFactor">Trades per year for Sharpe annualization (default 365).</param>
    /// <param name="seed">RNG seed for reproducibility.</param>
    public static PermutationTestResult RunPnlPermutation(
        ReadOnlySpan<double> tradePnls,
        double initialEquity,
        int iterations,
        double annualizationFactor = 365,
        int seed = 42)
    {
        if (tradePnls.Length < 2)
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

        var source = tradePnls.ToArray();
        var observedSharpe = ComputeSharpe(source, initialEquity, annualizationFactor);

        var permutedSharpes = new double[iterations];
        var totalExceed = 0;

        // Sequential iteration with array reuse
        var rng = new Random(seed);
        var shuffled = new double[source.Length];

        for (var i = 0; i < iterations; i++)
        {
            Array.Copy(source, shuffled, source.Length);
            StatisticalUtils.FisherYatesShuffle(shuffled, rng);

            var permSharpe = ComputeSharpe(shuffled, initialEquity, annualizationFactor);
            permutedSharpes[i] = permSharpe;
            if (permSharpe >= observedSharpe)
                totalExceed++;
        }

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
    /// Computes annualized Sharpe ratio from trade P&amp;L. Returns are computed as
    /// tradePnl[i] / equity[i-1] to capture proportional returns.
    /// </summary>
    internal static double ComputeSharpe(double[] tradePnls, double initialEquity, double annualizationFactor)
    {
        if (tradePnls.Length < 2) return 0.0;

        var n = tradePnls.Length;
        var sumReturn = 0.0;
        var sumReturnSq = 0.0;
        var equity = initialEquity;

        for (var i = 0; i < n; i++)
        {
            var ret = equity > 0 ? tradePnls[i] / equity : 0.0;
            sumReturn += ret;
            sumReturnSq += ret * ret;
            equity += tradePnls[i];
        }

        var meanReturn = sumReturn / n;
        var variance = sumReturnSq / n - meanReturn * meanReturn;
        if (variance <= 0) return 0.0;

        var stdev = Math.Sqrt(variance);
        return (meanReturn / stdev) * Math.Sqrt(annualizationFactor);
    }
}
