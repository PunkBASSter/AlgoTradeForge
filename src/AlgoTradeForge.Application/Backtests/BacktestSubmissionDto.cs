namespace AlgoTradeForge.Application.Backtests;

public sealed record BacktestSubmissionDto
{
    public required Guid Id { get; init; }
    public required int TotalBars { get; init; }
    public required bool IsDedup { get; init; }
}
