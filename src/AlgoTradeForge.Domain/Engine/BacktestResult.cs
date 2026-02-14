using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed record BacktestResult(
    Portfolio FinalPortfolio,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<Int64Bar> Bars,
    PerformanceMetrics Metrics,
    TimeSpan Duration);
