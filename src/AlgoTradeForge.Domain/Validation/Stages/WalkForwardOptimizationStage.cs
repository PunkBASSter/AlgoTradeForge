namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>Stage 4: Walk-forward optimization. Stub — passes all candidates.</summary>
public sealed class WalkForwardOptimizationStage : IValidationStage
{
    public int StageNumber => 4;
    public string StageName => "WalkForwardOptimization";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        foreach (var idx in context.ActiveCandidateIndices)
            verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "STUB", []));
        return new StageResult(context.ActiveCandidateIndices, verdicts);
    }
}
