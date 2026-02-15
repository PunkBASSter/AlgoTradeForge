using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Reporting;

public interface IMetricsCalculator
{
    PerformanceMetrics Calculate(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<decimal> equityCurve,
        decimal initialCash);
}
