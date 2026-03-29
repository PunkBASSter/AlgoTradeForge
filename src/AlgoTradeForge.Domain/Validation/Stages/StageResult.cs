namespace AlgoTradeForge.Domain.Validation.Stages;

public sealed record StageResult(
    IReadOnlyList<int> SurvivingIndices,
    IReadOnlyList<CandidateVerdict> Verdicts);
