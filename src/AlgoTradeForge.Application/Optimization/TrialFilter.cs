using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Optimization;

public sealed class TrialFilter
{
    private readonly double? _minProfitFactor;
    private readonly double? _maxDrawdownPct;
    private readonly double? _minSharpeRatio;
    private readonly double? _minSortinoRatio;
    private readonly double? _minAnnualizedReturnPct;

    public TrialFilter(double? minProfitFactor, double? maxDrawdownPct,
        double? minSharpeRatio, double? minSortinoRatio, double? minAnnualizedReturnPct)
    {
        _minProfitFactor = minProfitFactor;
        _maxDrawdownPct = maxDrawdownPct;
        _minSharpeRatio = minSharpeRatio;
        _minSortinoRatio = minSortinoRatio;
        _minAnnualizedReturnPct = minAnnualizedReturnPct;
    }

    public TrialFilter(RunOptimizationCommand c)
        : this(c.MinProfitFactor, c.MaxDrawdownPct, c.MinSharpeRatio, c.MinSortinoRatio, c.MinAnnualizedReturnPct) { }

    public TrialFilter(RunGeneticOptimizationCommand c)
        : this(c.MinProfitFactor, c.MaxDrawdownPct, c.MinSharpeRatio, c.MinSortinoRatio, c.MinAnnualizedReturnPct) { }

    public bool Passes(PerformanceMetrics m) =>
        (!_minProfitFactor.HasValue        || m.ProfitFactor        >= _minProfitFactor.Value) &&
        (!_maxDrawdownPct.HasValue         || m.MaxDrawdownPct      <= _maxDrawdownPct.Value) &&
        (!_minSharpeRatio.HasValue         || m.SharpeRatio         >= _minSharpeRatio.Value) &&
        (!_minSortinoRatio.HasValue        || m.SortinoRatio        >= _minSortinoRatio.Value) &&
        (!_minAnnualizedReturnPct.HasValue || m.AnnualizedReturnPct >= _minAnnualizedReturnPct.Value);
}
