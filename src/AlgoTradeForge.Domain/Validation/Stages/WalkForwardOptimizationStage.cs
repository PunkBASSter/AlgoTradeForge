namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 4: Walk-Forward Optimization. Validates whether the optimization process
/// generalizes across time by running rolling IS/OOS windows on the entire trial pool.
/// This is a whole-pool gate — if WFO fails, all candidates are rejected.
/// </summary>
public sealed class WalkForwardOptimizationStage : IValidationStage
{
    public int StageNumber => 4;
    public string StageName => "WalkForwardOptimization";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.WalkForwardOptimization;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);

        // Build WFO config from thresholds
        var config = new WfoConfig
        {
            WindowCount = thresholds.MinWfoRuns,
            OosPct = thresholds.OosPct,
            MinWfe = thresholds.MinWfe,
            MinProfitableWindowsPct = thresholds.MinProfitableWindowsPct,
            MaxOosDrawdownExcess = thresholds.MaxOosDrawdownExcess,
        };

        // Get initial equity from first trial
        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        var wfoResult = WalkForwardEngine.RunWfo(context.Cache, config, initialEquity, ct);

        // Determine gate-level pass/fail reason
        string? gateReason = null;
        if (wfoResult.WalkForwardEfficiency < thresholds.MinWfe)
            gateReason = "WFE_BELOW_THRESHOLD";
        else if (wfoResult.ProfitableWindowsPct < thresholds.MinProfitableWindowsPct)
            gateReason = "INSUFFICIENT_PROFITABLE_WINDOWS";
        else if (wfoResult.MaxOosDrawdownExcessPct > thresholds.MaxOosDrawdownExcess)
            gateReason = "OOS_DRAWDOWN_EXCESSIVE";

        var passed = gateReason is null;

        foreach (var idx in context.ActiveCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var metrics = new Dictionary<string, double>
            {
                ["wfe"] = wfoResult.WalkForwardEfficiency,
                ["profitableWindowsPct"] = wfoResult.ProfitableWindowsPct,
                ["oosDrawdownExcessPct"] = wfoResult.MaxOosDrawdownExcessPct,
                ["windowCount"] = wfoResult.Windows.Count,
            };

            if (passed)
            {
                survivors.Add(idx);
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, null, metrics));
            }
            else
            {
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, false, gateReason, metrics));
            }
        }

        return new StageResult(survivors, verdicts);
    }
}
