namespace AlgoTradeForge.WebApi.Contracts;

public sealed record BacktestSubmissionResponse
{
    public required Guid Id { get; init; }
    public required int TotalBars { get; init; }
}

public sealed record OptimizationSubmissionResponse
{
    public required Guid Id { get; init; }
    public required long TotalCombinations { get; init; }
}
