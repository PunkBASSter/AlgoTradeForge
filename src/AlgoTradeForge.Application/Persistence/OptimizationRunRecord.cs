namespace AlgoTradeForge.Application.Persistence;

public sealed record OptimizationRunRecord
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required long DurationMs { get; init; }
    public required long TotalCombinations { get; init; }
    public required string SortBy { get; init; }
    public required DateTimeOffset DataStart { get; init; }
    public required DateTimeOffset DataEnd { get; init; }
    public required decimal InitialCash { get; init; }
    public required decimal Commission { get; init; }
    public required int SlippageTicks { get; init; }
    public required int MaxParallelism { get; init; }
    public required IReadOnlyList<DataSubscriptionRecord> DataSubscriptions { get; init; }
    public required IReadOnlyList<BacktestRunRecord> Trials { get; init; }
}
