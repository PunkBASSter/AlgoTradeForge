namespace AlgoTradeForge.Domain.Reporting;

public sealed record PerformanceMetrics
{
    public required int TotalTrades { get; init; }
    public required int WinningTrades { get; init; }
    public required int LosingTrades { get; init; }
    public required decimal NetProfit { get; init; }
    public required decimal GrossProfit { get; init; }
    public required decimal GrossLoss { get; init; }
    public required double TotalReturnPct { get; init; }
    public required double AnnualizedReturnPct { get; init; }
    public required double SharpeRatio { get; init; }
    public required double SortinoRatio { get; init; }
    public required double MaxDrawdownPct { get; init; }
    public required double WinRatePct { get; init; }
    public required double ProfitFactor { get; init; }
    public required double AverageWin { get; init; }
    public required double AverageLoss { get; init; }
    public required decimal InitialCapital { get; init; }
    public required decimal FinalEquity { get; init; }
    public required int TradingDays { get; init; }
}
