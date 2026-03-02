using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Optimization;

public sealed class TrialFilter(RunOptimizationCommand command)
{
    public bool Passes(PerformanceMetrics m) =>
        (!command.MinProfitFactor.HasValue        || m.ProfitFactor        >= command.MinProfitFactor.Value) &&
        (!command.MaxDrawdownPct.HasValue         || m.MaxDrawdownPct      <= command.MaxDrawdownPct.Value) &&
        (!command.MinSharpeRatio.HasValue         || m.SharpeRatio         >= command.MinSharpeRatio.Value) &&
        (!command.MinSortinoRatio.HasValue        || m.SortinoRatio        >= command.MinSortinoRatio.Value) &&
        (!command.MinAnnualizedReturnPct.HasValue || m.AnnualizedReturnPct >= command.MinAnnualizedReturnPct.Value);
}
