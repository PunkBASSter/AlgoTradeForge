using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Optimization;

public sealed class TrialFilter
{
    private readonly double? _minProfitFactor;
    private readonly double? _maxDrawdownPct;
    private readonly double? _minSharpeRatio;
    private readonly double? _minSortinoRatio;
    private readonly double? _minAnnualizedReturnPct;

    public TrialFilter(RunOptimizationCommand command)
    {
        _minProfitFactor = command.MinProfitFactor;
        _maxDrawdownPct = command.MaxDrawdownPct;
        _minSharpeRatio = command.MinSharpeRatio;
        _minSortinoRatio = command.MinSortinoRatio;
        _minAnnualizedReturnPct = command.MinAnnualizedReturnPct;
    }

    public TrialFilter(RunGeneticOptimizationCommand command)
    {
        _minProfitFactor = command.MinProfitFactor;
        _maxDrawdownPct = command.MaxDrawdownPct;
        _minSharpeRatio = command.MinSharpeRatio;
        _minSortinoRatio = command.MinSortinoRatio;
        _minAnnualizedReturnPct = command.MinAnnualizedReturnPct;
    }

    public bool Passes(PerformanceMetrics m) =>
        (!_minProfitFactor.HasValue        || m.ProfitFactor        >= _minProfitFactor.Value) &&
        (!_maxDrawdownPct.HasValue         || m.MaxDrawdownPct      <= _maxDrawdownPct.Value) &&
        (!_minSharpeRatio.HasValue         || m.SharpeRatio         >= _minSharpeRatio.Value) &&
        (!_minSortinoRatio.HasValue        || m.SortinoRatio        >= _minSortinoRatio.Value) &&
        (!_minAnnualizedReturnPct.HasValue || m.AnnualizedReturnPct >= _minAnnualizedReturnPct.Value);
}
