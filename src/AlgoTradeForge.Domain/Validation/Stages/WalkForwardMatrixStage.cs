namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>Stage 5: Walk-forward matrix. Stub — passes all candidates.</summary>
public sealed class WalkForwardMatrixStage : IValidationStage
{
    public int StageNumber => 5;
    public string StageName => "WalkForwardMatrix";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        foreach (var idx in context.ActiveCandidateIndices)
            verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "STUB", []));
        return new StageResult(context.ActiveCandidateIndices, verdicts);
    }
}
