namespace AlgoTradeForge.Application.Persistence;

public static class ValidationGroupStatus
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string PartiallyCompleted = "PartiallyCompleted";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed record ValidationGroupRecord
{
    public required Guid Id { get; init; }
    public required Guid OptimizationGroupId { get; init; }
    public required string StrategyName { get; init; }
    public required string ThresholdProfileName { get; init; }
    public string? ThresholdProfileJson { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required int TotalRuns { get; init; }
    public string Status { get; init; } = ValidationGroupStatus.InProgress;
    public IReadOnlyList<ValidationRunRecord> Runs { get; init; } = [];
}
