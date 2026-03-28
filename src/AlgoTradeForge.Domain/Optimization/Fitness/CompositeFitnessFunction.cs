using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Domain.Optimization.Fitness;

/// <summary>
/// Weighted composite fitness with drawdown and trade-count penalties.
/// <para>
/// fitness = w_sharpe * Sharpe
///         + w_sortino * Sortino * 0.7
///         + w_pf * min(ProfitFactor, 5.0)
///         + w_annRet * AnnualizedReturn / 100
///         - drawdownPenalty
///         - tradePenalty
/// </para>
/// </summary>
public sealed class CompositeFitnessFunction : IFitnessFunction
{
    private readonly FitnessWeights _weights;
    private readonly int _minTrades;
    private readonly double _maxDrawdownThreshold;

    public CompositeFitnessFunction(FitnessConfig config)
    {
        _weights = config.Weights ?? new FitnessWeights();
        _minTrades = config.MinTrades;
        _maxDrawdownThreshold = config.MaxDrawdownThreshold;
    }

    public CompositeFitnessFunction(FitnessWeights? weights = null, int minTrades = 10, double maxDrawdownThreshold = 30.0)
    {
        _weights = weights ?? new FitnessWeights();
        _minTrades = minTrades;
        _maxDrawdownThreshold = maxDrawdownThreshold;
    }

    public double Evaluate(PerformanceMetrics metrics)
    {
        if (metrics.TotalTrades == 0)
            return double.MinValue;

        var sharpe = Sanitize(metrics.SharpeRatio);
        var sortino = Sanitize(metrics.SortinoRatio);
        var pf = Sanitize(metrics.ProfitFactor);
        var annRet = Sanitize(metrics.AnnualizedReturnPct);
        var dd = Sanitize(metrics.MaxDrawdownPct);

        var fitness = _weights.SharpeWeight * sharpe
                    + _weights.SortinoWeight * sortino * 0.7
                    + _weights.ProfitFactorWeight * Math.Min(pf, 5.0)
                    + _weights.AnnualizedReturnWeight * annRet / 100.0;

        // Quadratic drawdown penalty — strongly penalizes extreme DD
        var ddExcess = Math.Max(0, dd - _maxDrawdownThreshold);
        fitness -= ddExcess * ddExcess * 0.01;

        // Trade count penalty — linear ramp when below minimum
        if (metrics.TotalTrades < _minTrades)
        {
            var tradePenalty = (_minTrades - metrics.TotalTrades) / (double)_minTrades * 2.0;
            fitness -= tradePenalty;
        }

        return fitness;
    }

    private static double Sanitize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
}
