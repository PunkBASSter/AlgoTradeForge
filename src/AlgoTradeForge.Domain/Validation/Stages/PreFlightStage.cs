namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>Stage 0: Pre-flight checks. Placeholder for Phase 5 — passes all candidates.</summary>
public sealed class PreFlightStage : IValidationStage
{
    public int StageNumber => 0;
    public string StageName => "PreFlight";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        foreach (var idx in context.ActiveCandidateIndices)
        {
            var trial = context.Trials[idx];
            verdicts.Add(new CandidateVerdict(trial.Id, true, "STUB", []));
        }

        return new StageResult(context.ActiveCandidateIndices, verdicts);
    }
}
