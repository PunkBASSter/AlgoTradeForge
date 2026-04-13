using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Validation;

public sealed record RunGroupValidationCommand : ICommand<ValidationGroupSubmissionDto>
{
    public required Guid OptimizationGroupId { get; init; }
    public string ThresholdProfileName { get; init; } = "Crypto-Standard";
    public int MaxTrialsToValidate { get; init; } = 100;
}

public sealed record ValidationGroupSubmissionDto
{
    public required Guid GroupId { get; init; }
    public required IReadOnlyList<ValidationGroupRunDto> Runs { get; init; }
}

public sealed record ValidationGroupRunDto
{
    public required Guid Id { get; init; }
    public required Guid OptimizationRunId { get; init; }
    public required int CandidateCount { get; init; }
}
