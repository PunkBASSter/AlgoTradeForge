namespace AlgoTradeForge.WebApi.Contracts;

public sealed record BacktestStatusResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required int ProcessedBars { get; init; }
    public required int TotalBars { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
    public BacktestRunResponse? Result { get; init; }
}

public sealed record OptimizationStatusResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required long CompletedCombinations { get; init; }
    public required long FailedCombinations { get; init; }
    public required long TotalCombinations { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
    public OptimizationRunResponse? Result { get; init; }
}
