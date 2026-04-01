namespace AlgoTradeForge.Application.Persistence;

public static class OptimizationRunStatus
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    /// <summary>Well-known error message for cancelled runs — used to distinguish from failures.</summary>
    public const string CancelledMessage = "Run was cancelled by user.";

    public static string FromError(string? errorMessage) =>
        errorMessage is null             ? Completed :
        errorMessage == CancelledMessage ? Cancelled :
                                           Failed;
}

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
    public long DedupSkipped { get; init; }
    public required IReadOnlyList<BacktestRunRecord> Trials { get; init; }
    public IReadOnlyList<FailedTrialRecord> FailedTrialDetails { get; init; } = [];
    public string? InputJson { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
    public string? OptimizationMethod { get; init; }
    public int? GenerationsCompleted { get; init; }
    public string Status { get; init; } = OptimizationRunStatus.Completed;
}
