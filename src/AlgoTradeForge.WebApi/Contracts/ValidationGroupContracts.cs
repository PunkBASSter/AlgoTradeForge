namespace AlgoTradeForge.WebApi.Contracts;

public sealed record RunGroupValidationRequest
{
    public required Guid OptimizationGroupId { get; init; }
    public string ThresholdProfileName { get; init; } = "Crypto-Standard";
    public int MaxTrialsToValidate { get; init; } = 100;
}

public sealed record ValidationGroupSubmissionResponse
{
    public required Guid GroupId { get; init; }
    public required List<ValidationGroupRunSubmission> Runs { get; init; }
}

public sealed record ValidationGroupRunSubmission
{
    public required Guid Id { get; init; }
    public required Guid OptimizationRunId { get; init; }
    public required int CandidateCount { get; init; }
}

public sealed record ValidationGroupDetailResponse
{
    public required Guid Id { get; init; }
    public required Guid OptimizationGroupId { get; init; }
    public required string StrategyName { get; init; }
    public required string ThresholdProfileName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required int TotalRuns { get; init; }
    public required List<ValidationGroupRunDetailResponse> Runs { get; init; }
}

public sealed record ValidationGroupRunDetailResponse
{
    public required Guid Id { get; init; }
    public required Guid OptimizationRunId { get; init; }
    public required List<DataSubscriptionInput> Dss { get; init; }
    public required string Status { get; init; }
    public int CandidatesIn { get; init; }
    public int CandidatesOut { get; init; }
    public double CompositeScore { get; init; }
    public required string Verdict { get; init; }
}

public sealed record ValidationGroupStatusResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required List<GroupRunStatusResponse> Runs { get; init; }
}
