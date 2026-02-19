using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed record BacktestResult(
    Portfolio FinalPortfolio,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<long> EquityCurve,
    int TotalBarsProcessed,
    TimeSpan Duration);
