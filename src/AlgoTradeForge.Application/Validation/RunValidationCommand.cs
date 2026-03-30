using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Validation;

public sealed record RunValidationCommand : ICommand<ValidationSubmissionDto>
{
    public required Guid OptimizationRunId { get; init; }
    public string ThresholdProfileName { get; init; } = "Crypto-Standard";
}

public sealed record ValidationSubmissionDto(Guid Id, int CandidateCount);
