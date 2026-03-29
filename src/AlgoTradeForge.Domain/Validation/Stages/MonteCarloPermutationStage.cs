namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>Stage 6: Monte Carlo permutation test. Stub — passes all candidates.</summary>
public sealed class MonteCarloPermutationStage : IValidationStage
{
    public int StageNumber => 6;
    public string StageName => "MonteCarloPermutation";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        foreach (var idx in context.ActiveCandidateIndices)
            verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "STUB", []));
        return new StageResult(context.ActiveCandidateIndices, verdicts);
    }
}
