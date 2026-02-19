using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Reporting;

public interface IMetricsCalculator
{
    PerformanceMetrics Calculate(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<long> equityCurve,
        long initialCash,
        DateTimeOffset startTime,
        DateTimeOffset endTime);
}
