namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Lightweight performance metrics computed from a P&amp;L delta array over a bar window.
/// Used by WFO to evaluate IS/OOS performance without full PerformanceMetrics overhead.
/// </summary>
public sealed record WindowPerformanceMetrics
{
    public required double TotalReturnPct { get; init; }
    public required double AnnualizedReturnPct { get; init; }
    public required double SharpeRatio { get; init; }
    public required double MaxDrawdownPct { get; init; }
    public required double ProfitFactor { get; init; }
    public required int BarCount { get; init; }
}
