using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record OptimizationGroupSubmissionResponse
{
    public required Guid GroupId { get; init; }
    public required List<GroupRunSubmission> Runs { get; init; }
    public required long TotalCombinationsPerRun { get; init; }
}

public sealed record GroupRunSubmission
{
    public required Guid Id { get; init; }
    public required List<DataFeedSubscription> Dss { get; init; }
    public required long TotalCombinations { get; init; }
}

public sealed record OptimizationGroupSummaryResponse
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required string OptimizationMethod { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required int TotalRuns { get; init; }
    public int CompletedRuns { get; init; }
    public int FailedRuns { get; init; }
    public required string Status { get; init; }
    public required List<List<DataFeedSubscription>> Subscriptions { get; init; }
}

public sealed record OptimizationGroupDetailResponse
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required string OptimizationMethod { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required int TotalRuns { get; init; }
    public int CompletedRuns { get; init; }
    public int FailedRuns { get; init; }
    public required string Status { get; init; }
    public required List<List<DataFeedSubscription>> Subscriptions { get; init; }
    public required int MaxParallelism { get; init; }
    public string? InputJson { get; init; }
    public required List<GroupRunDetailResponse> Runs { get; init; }
}

public sealed record GroupRunDetailResponse
{
    public required Guid Id { get; init; }
    public required List<DataFeedSubscription> Dss { get; init; }
    public required string Status { get; init; }
    public required long TotalCombinations { get; init; }
    public int KeptTrials { get; init; }
    public long FilteredTrials { get; init; }
    public long FailedTrials { get; init; }
    public long DurationMs { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record OptimizationGroupStatusResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required List<GroupRunStatusResponse> Runs { get; init; }
}

public sealed record GroupRunStatusResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public long Processed { get; init; }
    public long Total { get; init; }
}
