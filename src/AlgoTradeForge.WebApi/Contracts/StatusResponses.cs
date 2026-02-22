namespace AlgoTradeForge.WebApi.Contracts;

public sealed record BacktestStatusResponse
{
    public required Guid Id { get; init; }
    public BacktestRunResponse? Result { get; init; }
}

public sealed record OptimizationStatusResponse
{
    public required Guid Id { get; init; }
    public long CompletedCombinations { get; init; }
    public long TotalCombinations { get; init; }
    public OptimizationRunResponse? Result { get; init; }
}
