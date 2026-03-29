using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Decomposes equity curve into equal sub-periods, computes per-period metrics,
/// and measures consistency via Sharpe coefficient of variation and equity curve R².
/// </summary>
public static class SubPeriodAnalyzer
{
    /// <summary>
    /// Analyzes P&amp;L delta consistency across equal sub-periods.
    /// </summary>
    /// <param name="pnlDeltas">Per-bar P&amp;L deltas.</param>
    /// <param name="initialEquity">Starting equity.</param>
    /// <param name="numPeriods">Number of equal sub-periods to divide bars into.</param>
    /// <param name="annualizationFactor">Bars per year for Sharpe annualization.</param>
    public static SubPeriodConsistencyResult Analyze(
        ReadOnlySpan<double> pnlDeltas,
        double initialEquity,
        int numPeriods,
        double annualizationFactor = 365)
    {
        if (pnlDeltas.Length < numPeriods || numPeriods < 1)
        {
            return new SubPeriodConsistencyResult
            {
                ProfitableSubPeriodsPct = 0,
                SharpeCoeffOfVariation = 0,
                EquityCurveR2 = 0,
                SubPeriods = [],
            };
        }

        var n = pnlDeltas.Length;
        var periodSize = n / numPeriods;
        var subPeriods = new List<SubPeriodMetrics>(numPeriods);

        // Compute equity at the start of each sub-period
        var equityAtStart = initialEquity;
        for (var p = 0; p < numPeriods; p++)
        {
            var start = p * periodSize;
            var end = p == numPeriods - 1 ? n : (p + 1) * periodSize;
            var length = end - start;

            var periodPnl = pnlDeltas.Slice(start, length);
            var metrics = WindowMetricsCalculator.Compute(periodPnl, equityAtStart, annualizationFactor);

            subPeriods.Add(new SubPeriodMetrics
            {
                StartBar = start,
                EndBar = end,
                Sharpe = metrics.SharpeRatio,
                ReturnPct = metrics.TotalReturnPct,
                ProfitFactor = metrics.ProfitFactor,
            });

            // Advance equity to the start of next period
            for (var i = start; i < end; i++)
                equityAtStart += pnlDeltas[i];
        }

        // Profitable sub-periods percentage
        var profitableCount = subPeriods.Count(sp => sp.ReturnPct > 0);
        var profitablePct = (double)profitableCount / numPeriods;

        // Sharpe coefficient of variation
        var sharpes = subPeriods.Select(sp => sp.Sharpe).ToArray();
        var sharpeCoV = ComputeCoeffOfVariation(sharpes);

        // Equity curve R²
        var r2 = ComputeEquityCurveR2(pnlDeltas, initialEquity);

        return new SubPeriodConsistencyResult
        {
            ProfitableSubPeriodsPct = profitablePct,
            SharpeCoeffOfVariation = sharpeCoV,
            EquityCurveR2 = r2,
            SubPeriods = subPeriods,
        };
    }

    /// <summary>
    /// Coefficient of variation: stdev / |mean|. Returns 0 if mean is 0.
    /// </summary>
    private static double ComputeCoeffOfVariation(double[] values)
    {
        if (values.Length < 2) return 0.0;

        var sum = 0.0;
        for (var i = 0; i < values.Length; i++)
            sum += values[i];

        var mean = sum / values.Length;
        if (Math.Abs(mean) < 1e-15) return 0.0;

        var sumSqDev = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            var dev = values[i] - mean;
            sumSqDev += dev * dev;
        }

        var stdev = Math.Sqrt(sumSqDev / values.Length);
        return stdev / Math.Abs(mean);
    }

    /// <summary>
    /// R² of linear regression on cumulative equity vs bar index.
    /// R² = 1 - SS_res / SS_tot. Perfect linear growth → R² = 1.
    /// </summary>
    internal static double ComputeEquityCurveR2(ReadOnlySpan<double> pnlDeltas, double initialEquity)
    {
        var n = pnlDeltas.Length;
        if (n < 2) return 0.0;

        // Build cumulative equity
        var equity = new double[n];
        var cumulative = initialEquity;
        for (var i = 0; i < n; i++)
        {
            cumulative += pnlDeltas[i];
            equity[i] = cumulative;
        }

        // Linear regression: y = a + b*x, where x = bar index, y = equity
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXY = 0.0;
        var sumX2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            sumX += i;
            sumY += equity[i];
            sumXY += i * equity[i];
            sumX2 += (double)i * i;
        }

        var meanX = sumX / n;
        var meanY = sumY / n;

        var ssXY = sumXY - n * meanX * meanY;
        var ssX2 = sumX2 - n * meanX * meanX;

        if (ssX2 < 1e-15) return 0.0;

        var slope = ssXY / ssX2;
        var intercept = meanY - slope * meanX;

        // R² = 1 - SS_res / SS_tot
        var ssTot = 0.0;
        var ssRes = 0.0;
        for (var i = 0; i < n; i++)
        {
            var predicted = intercept + slope * i;
            ssRes += (equity[i] - predicted) * (equity[i] - predicted);
            ssTot += (equity[i] - meanY) * (equity[i] - meanY);
        }

        return ssTot > 0 ? Math.Max(0, 1.0 - ssRes / ssTot) : 0.0;
    }
}
