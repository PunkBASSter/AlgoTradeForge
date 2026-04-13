namespace AlgoTradeForge.Application.Persistence;

public static class OptimizationGroupStatus
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string PartiallyCompleted = "PartiallyCompleted";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed record OptimizationGroupRecord
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public string? StrategyVersion { get; init; }
    public required string OptimizationMethod { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required int TotalRuns { get; init; }
    public string Status { get; init; } = OptimizationGroupStatus.InProgress;
    public string? InputJson { get; init; }
    public required string SubscriptionsJson { get; init; }
    public required string BacktestSettingsJson { get; init; }
    public string? OptimizationSettingsJson { get; init; }
    public string? FitnessConfigJson { get; init; }
    public required int MaxParallelism { get; init; }
    public IReadOnlyList<OptimizationRunRecord> Runs { get; init; } = [];
}
