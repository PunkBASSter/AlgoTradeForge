namespace AlgoTradeForge.Application.Persistence;

public static class ValidationRunStatus
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed record ValidationRunRecord
{
    public required Guid Id { get; init; }
    public required Guid OptimizationRunId { get; init; }
    public required string StrategyName { get; init; }
    public string? StrategyVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public string Status { get; init; } = ValidationRunStatus.InProgress;
    public required string ThresholdProfileName { get; init; }
    public string? ThresholdProfileJson { get; init; }
    public int CandidatesIn { get; init; }
    public int CandidatesOut { get; init; }
    public double CompositeScore { get; init; }
    public string Verdict { get; init; } = "Red";
    public string? VerdictSummary { get; init; }
    public int InvocationCount { get; init; } = 1;
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<StageResultRecord> StageResults { get; init; } = [];
}

public sealed record StageResultRecord
{
    public required Guid ValidationRunId { get; init; }
    public required int StageNumber { get; init; }
    public required string StageName { get; init; }
    public int CandidatesIn { get; init; }
    public int CandidatesOut { get; init; }
    public long DurationMs { get; init; }
    public string? CandidateVerdictsJson { get; init; }
}
