namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>Stage 3: Parameter landscape analysis. Stub — passes all candidates.</summary>
public sealed class ParameterLandscapeStage : IValidationStage
{
    public int StageNumber => 3;
    public string StageName => "ParameterLandscape";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        foreach (var idx in context.ActiveCandidateIndices)
            verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "STUB", []));
        return new StageResult(context.ActiveCandidateIndices, verdicts);
    }
}
