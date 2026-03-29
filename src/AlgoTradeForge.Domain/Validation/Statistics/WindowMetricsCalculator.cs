using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Computes lightweight performance metrics from a P&amp;L delta array.
/// Operates on raw bar deltas — no PerformanceMetrics or full backtest required.
/// </summary>
public static class WindowMetricsCalculator
{
    /// <summary>
    /// Compute window metrics from P&amp;L deltas.
    /// </summary>
    /// <param name="pnlDeltas">Per-bar equity changes.</param>
    /// <param name="initialEquity">Starting equity for the window.</param>
    /// <param name="annualizationFactor">Bars per year (e.g., 365 for daily crypto).</param>
    public static WindowPerformanceMetrics Compute(
        ReadOnlySpan<double> pnlDeltas, double initialEquity, double annualizationFactor)
    {
        if (pnlDeltas.Length == 0 || initialEquity <= 0)
        {
            return new WindowPerformanceMetrics
            {
                TotalReturnPct = 0,
                AnnualizedReturnPct = 0,
                SharpeRatio = 0,
                MaxDrawdownPct = 0,
                ProfitFactor = 0,
                BarCount = pnlDeltas.Length,
            };
        }

        // Cumulative equity and per-bar returns
        var barCount = pnlDeltas.Length;
        var equity = initialEquity;
        var peak = equity;
        var maxDrawdown = 0.0;
        var sumPositive = 0.0;
        var sumNegative = 0.0;

        // For Sharpe: collect per-bar returns
        Span<double> returns = barCount <= 4096
            ? stackalloc double[barCount]
            : new double[barCount];

        for (var i = 0; i < barCount; i++)
        {
            var prevEquity = equity;
            equity += pnlDeltas[i];

            // Per-bar return
            returns[i] = prevEquity > 0 ? pnlDeltas[i] / prevEquity : 0;

            // Profit factor accumulators
            if (pnlDeltas[i] > 0)
                sumPositive += pnlDeltas[i];
            else
                sumNegative += Math.Abs(pnlDeltas[i]);

            // Max drawdown
            if (equity > peak) peak = equity;
            var dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }

        var totalReturn = (equity - initialEquity) / initialEquity;
        var totalReturnPct = totalReturn * 100.0;

        // Annualized return: (1 + totalReturn)^(annFactor/bars) - 1
        var annualizedReturn = barCount > 0 && totalReturn > -1
            ? (Math.Pow(1 + totalReturn, annualizationFactor / barCount) - 1) * 100.0
            : 0.0;

        // Sharpe ratio: mean(returns) / stdev(returns) × sqrt(annualizationFactor)
        var sharpe = ComputeSharpe(returns, annualizationFactor);

        // Profit factor
        var profitFactor = sumNegative > 0 ? sumPositive / sumNegative : (sumPositive > 0 ? double.MaxValue : 0);

        return new WindowPerformanceMetrics
        {
            TotalReturnPct = Sanitize(totalReturnPct),
            AnnualizedReturnPct = Sanitize(annualizedReturn),
            SharpeRatio = Sanitize(sharpe),
            MaxDrawdownPct = Sanitize(maxDrawdown * 100.0),
            ProfitFactor = Sanitize(profitFactor),
            BarCount = barCount,
        };
    }

    private static double ComputeSharpe(ReadOnlySpan<double> returns, double annualizationFactor)
    {
        if (returns.Length < 2) return 0;

        var sum = 0.0;
        for (var i = 0; i < returns.Length; i++)
            sum += returns[i];
        var mean = sum / returns.Length;

        var sumSq = 0.0;
        for (var i = 0; i < returns.Length; i++)
        {
            var diff = returns[i] - mean;
            sumSq += diff * diff;
        }

        var stdev = Math.Sqrt(sumSq / (returns.Length - 1));
        if (stdev <= 1e-15) return 0;

        return (mean / stdev) * Math.Sqrt(annualizationFactor);
    }

    private static double Sanitize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
}
