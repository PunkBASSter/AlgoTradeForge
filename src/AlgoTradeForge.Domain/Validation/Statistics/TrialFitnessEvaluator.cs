using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Evaluates composite fitness from <see cref="PerformanceMetrics"/> using default weights.
/// Mirrors <c>CompositeFitnessFunction</c> with fixed defaults (no DI required).
/// Used by validation stages that need trial-level ranking without configurable weights.
/// </summary>
public static class TrialFitnessEvaluator
{
    private const double SharpeWeight = 0.5;
    private const double SortinoWeight = 0.2;
    private const double ProfitFactorWeight = 0.15;
    private const double AnnualizedReturnWeight = 0.15;
    private const double MaxDrawdownThreshold = 30.0;
    private const int MinTrades = 10;

    public static double Evaluate(PerformanceMetrics metrics)
    {
        if (metrics.TotalTrades == 0)
            return double.MinValue;

        var sharpe = Sanitize(metrics.SharpeRatio);
        var sortino = Sanitize(metrics.SortinoRatio);
        var pf = Sanitize(metrics.ProfitFactor);
        var annRet = Sanitize(metrics.AnnualizedReturnPct);
        var dd = Sanitize(metrics.MaxDrawdownPct);

        var fitness = SharpeWeight * sharpe
                    + SortinoWeight * sortino * 0.7
                    + ProfitFactorWeight * Math.Min(pf, 5.0)
                    + AnnualizedReturnWeight * annRet / 100.0;

        var ddExcess = Math.Max(0, dd - MaxDrawdownThreshold);
        fitness -= ddExcess * ddExcess * 0.01;

        if (metrics.TotalTrades < MinTrades)
            fitness -= (MinTrades - metrics.TotalTrades) / (double)MinTrades * 2.0;

        return fitness;
    }

    internal static double Sanitize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
}
