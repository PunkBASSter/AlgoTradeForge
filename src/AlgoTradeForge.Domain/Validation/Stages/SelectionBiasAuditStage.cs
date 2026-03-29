namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>Stage 7: Selection bias audit (CSCV/PBO). Stub — passes all candidates.</summary>
public sealed class SelectionBiasAuditStage : IValidationStage
{
    public int StageNumber => 7;
    public string StageName => "SelectionBiasAudit";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        foreach (var idx in context.ActiveCandidateIndices)
            verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "STUB", []));
        return new StageResult(context.ActiveCandidateIndices, verdicts);
    }
}
