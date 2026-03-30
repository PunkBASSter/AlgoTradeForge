using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Evaluates a composite fitness score from <see cref="WindowPerformanceMetrics"/>.
/// Mirrors the <c>CompositeFitnessFunction</c> formula but adapted for window-level metrics
/// (no Sortino available — Sharpe gets the combined weight).
/// </summary>
public static class WindowFitnessEvaluator
{
    private const double SharpeWeight = 0.70;       // Sharpe absorbs Sortino's 0.20 weight
    private const double ProfitFactorWeight = 0.15;
    private const double AnnualizedReturnWeight = 0.15;
    private const double MaxDrawdownThreshold = 30.0;

    public static double Evaluate(WindowPerformanceMetrics metrics)
    {
        if (metrics.BarCount == 0) return double.MinValue;

        var sharpe = Sanitize(metrics.SharpeRatio);
        var pf = Sanitize(metrics.ProfitFactor);
        var annRet = Sanitize(metrics.AnnualizedReturnPct);
        var dd = Sanitize(metrics.MaxDrawdownPct);

        var fitness = SharpeWeight * sharpe
                    + ProfitFactorWeight * Math.Min(pf, 5.0)
                    + AnnualizedReturnWeight * annRet / 100.0;

        // Quadratic drawdown penalty — same as CompositeFitnessFunction
        var ddExcess = Math.Max(0, dd - MaxDrawdownThreshold);
        fitness -= ddExcess * ddExcess * 0.01;

        return fitness;
    }

    private static double Sanitize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
}
