namespace AlgoTradeForge.Domain.Validation.Stages;

public sealed class ValidationContext
{
    public required SimulationCache Cache { get; init; }
    public required IReadOnlyList<TrialSummary> Trials { get; init; }
    public required ValidationThresholdProfile Profile { get; init; }
    public required IReadOnlyList<int> ActiveCandidateIndices { get; set; }
}
