namespace AlgoTradeForge.Application.Backtests;

public sealed record BacktestResultDto
{
    public required Guid Id { get; init; }
    public required string AssetName { get; init; }
    public required string StrategyName { get; init; }
    public required decimal InitialCapital { get; init; }
    public required decimal FinalEquity { get; init; }
    public required decimal NetProfit { get; init; }
    public required decimal TotalCommissions { get; init; }
    public required double TotalReturnPct { get; init; }
    public required double AnnualizedReturnPct { get; init; }
    public required double SharpeRatio { get; init; }
    public required double SortinoRatio { get; init; }
    public required double MaxDrawdownPct { get; init; }
    public required int TotalTrades { get; init; }
    public required double WinRatePct { get; init; }
    public required double ProfitFactor { get; init; }
    public required int TradingDays { get; init; }
    public required TimeSpan Duration { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
