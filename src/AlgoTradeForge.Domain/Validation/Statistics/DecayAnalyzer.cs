using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Detects alpha erosion by computing rolling Sharpe ratio over time
/// and fitting a linear regression to detect downward trends.
/// </summary>
public static class DecayAnalyzer
{
    /// <summary>
    /// Analyzes P&amp;L deltas for alpha decay via rolling Sharpe slope.
    /// </summary>
    /// <param name="pnlDeltas">Per-bar P&amp;L deltas.</param>
    /// <param name="initialEquity">Starting equity for return computation.</param>
    /// <param name="rollingWindow">Window size for rolling Sharpe computation.</param>
    /// <param name="annualizationFactor">Bars per year for Sharpe annualization.</param>
    public static DecayAnalysisResult Analyze(
        ReadOnlySpan<double> pnlDeltas,
        double initialEquity,
        int rollingWindow,
        double annualizationFactor = 365)
    {
        if (pnlDeltas.Length <= rollingWindow)
        {
            return new DecayAnalysisResult
            {
                RollingSharpe = [],
                SlopeCoefficient = 0.0,
                IsDecaying = false,
            };
        }

        var n = pnlDeltas.Length;

        // Pre-compute per-bar returns
        var returns = new double[n];
        var equity = initialEquity;
        for (var i = 0; i < n; i++)
        {
            returns[i] = equity > 0 ? pnlDeltas[i] / equity : 0.0;
            equity += pnlDeltas[i];
        }

        // Compute rolling Sharpe for each bar from rollingWindow onwards
        var rollingSharpe = new List<(int BarIndex, double Sharpe)>();

        for (var i = rollingWindow; i < n; i++)
        {
            var sum = 0.0;
            var sumSq = 0.0;
            for (var j = i - rollingWindow; j < i; j++)
            {
                sum += returns[j];
                sumSq += returns[j] * returns[j];
            }

            var mean = sum / rollingWindow;
            var variance = sumSq / rollingWindow - mean * mean;
            var stdev = Math.Sqrt(Math.Max(0, variance));
            var sharpe = stdev > 0 ? (mean / stdev) * Math.Sqrt(annualizationFactor) : 0.0;

            rollingSharpe.Add((i, sharpe));
        }

        // Linear regression: slope of rolling Sharpe vs bar index
        var slope = ComputeSlope(rollingSharpe);

        return new DecayAnalysisResult
        {
            RollingSharpe = rollingSharpe,
            SlopeCoefficient = slope,
            IsDecaying = slope < 0,
        };
    }

    private static double ComputeSlope(List<(int BarIndex, double Sharpe)> points)
    {
        if (points.Count < 2) return 0.0;

        var n = points.Count;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXY = 0.0;
        var sumX2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            double x = points[i].BarIndex;
            var y = points[i].Sharpe;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        var meanX = sumX / n;
        var denom = sumX2 - n * meanX * meanX;

        return denom > 0 ? (sumXY - n * meanX * (sumY / n)) / denom : 0.0;
    }
}
