using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Reporting;

public interface IMetricsCalculator
{
    PerformanceMetrics Calculate(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Int64Bar> bars,
        Portfolio portfolio,
        long finalPrice,
        Asset asset);
}
