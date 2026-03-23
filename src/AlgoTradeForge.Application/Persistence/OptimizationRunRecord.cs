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
    public required DataSubscriptionDto DataSubscription { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required int MaxParallelism { get; init; }
    public long FilteredTrials { get; init; }
    public long FailedTrials { get; init; }
    public required IReadOnlyList<BacktestRunRecord> Trials { get; init; }
    public IReadOnlyList<FailedTrialRecord> FailedTrialDetails { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
}
