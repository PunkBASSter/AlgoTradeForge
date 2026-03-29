namespace AlgoTradeForge.WebApi.Contracts;

public sealed record RunValidationRequest
{
    public required Guid OptimizationRunId { get; init; }
    public string ThresholdProfileName { get; init; } = "Crypto-Standard";
}

public sealed record ValidationSubmissionResponse
{
    public required Guid Id { get; init; }
    public required int CandidateCount { get; init; }
}

public sealed record ValidationStatusResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public int CurrentStage { get; init; }
    public int TotalStages { get; init; }
    public ValidationRunResponse? Result { get; init; }
}

public sealed record ValidationRunResponse
{
    public required Guid Id { get; init; }
    public required Guid OptimizationRunId { get; init; }
    public required string StrategyName { get; init; }
    public string? StrategyVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public required string Status { get; init; }
    public required string ThresholdProfileName { get; init; }
    public int CandidatesIn { get; init; }
    public int CandidatesOut { get; init; }
    public double CompositeScore { get; init; }
    public required string Verdict { get; init; }
    public string? VerdictSummary { get; init; }
    public int InvocationCount { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<StageResultResponse> StageResults { get; init; } = [];
}

public sealed record StageResultResponse
{
    public required int StageNumber { get; init; }
    public required string StageName { get; init; }
    public int CandidatesIn { get; init; }
    public int CandidatesOut { get; init; }
    public long DurationMs { get; init; }
    public string? DetailJson { get; init; }
}
