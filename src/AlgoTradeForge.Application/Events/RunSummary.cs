namespace AlgoTradeForge.Application.Events;

public sealed record RunSummary(
    int TotalBarsProcessed,
    long FinalEquity,
    int TotalFills,
    TimeSpan Duration);
