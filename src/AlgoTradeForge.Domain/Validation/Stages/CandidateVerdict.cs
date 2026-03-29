namespace AlgoTradeForge.Domain.Validation.Stages;

public sealed record CandidateVerdict(
    Guid TrialId,
    bool Passed,
    string? ReasonCode,
    Dictionary<string, double> Metrics);
