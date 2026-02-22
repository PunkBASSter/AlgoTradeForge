namespace AlgoTradeForge.Application.Optimization;

public sealed record OptimizationSubmissionDto
{
    public required Guid Id { get; init; }
    public required long TotalCombinations { get; init; }
}
