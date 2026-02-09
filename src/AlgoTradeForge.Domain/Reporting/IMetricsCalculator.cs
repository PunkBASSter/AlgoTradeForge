using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Reporting;

public interface IMetricsCalculator
{
    PerformanceMetrics Calculate(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Bar> bars,
        Portfolio portfolio,
        decimal finalPrice,
        Asset asset);
}
