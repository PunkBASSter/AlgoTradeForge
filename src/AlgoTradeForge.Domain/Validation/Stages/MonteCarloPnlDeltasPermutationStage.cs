using AlgoTradeForge.Domain.Validation.Statistics;

namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 6: Monte Carlo bootstrap + permutation test + cost stress test.
/// Per-candidate evaluation: each surviving candidate is tested for path dependency
/// (bootstrap drawdown), sequential significance (permutation test), and cost sensitivity.
/// </summary>
public sealed class MonteCarloPnlDeltasPermutationStage : IValidationStage
{
    public int StageNumber => 6;
    public string StageName => "MonteCarloPermutation";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.MonteCarloPermutation;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);

        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        foreach (var idx in context.ActiveCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var trial = context.Trials[idx];
            var pnlDeltas = context.Cache.GetTrialPnl(idx);
            var metrics = new Dictionary<string, double>();

            string? failReason = null;

            // Check A: Bootstrap drawdown
            var mcResult = MonteCarloBootstrap.Run(
                pnlDeltas, initialEquity, thresholds.BootstrapIterations, seed: 42 + idx);

            var observedDdPct = trial.Metrics.MaxDrawdownPct;
            var bootstrapDd95 = mcResult.DrawdownPercentiles.TryGetValue(95, out var dd95) ? dd95 : 0.0;
            var ddMultiplier = observedDdPct > 0 ? bootstrapDd95 / observedDdPct : 0.0;

            metrics["bootstrapDd95"] = bootstrapDd95;
            metrics["observedDdPct"] = observedDdPct;
            metrics["ddMultiplier"] = ddMultiplier;
            metrics["probabilityOfRuin"] = mcResult.ProbabilityOfRuin;

            if (ddMultiplier > thresholds.MaxDrawdownMultiplier)
                failReason ??= "MC_DRAWDOWN_EXCESSIVE";

            // Check B: Permutation test
            var permResult = PermutationTester.RunPnlPermutation(
                pnlDeltas, initialEquity, thresholds.PermutationIterations, seed: 42 + idx);

            metrics["permutationPValue"] = permResult.PValue;
            metrics["observedSharpe"] = permResult.OriginalMetric;

            if (permResult.PValue >= thresholds.MaxPermutationPValue)
                failReason ??= "PERMUTATION_NOT_SIGNIFICANT";

            // Check C: Cost stress test
            var additionalCosts = trial.Metrics.TotalCommissions * ((decimal)thresholds.CostStressMultiplier - 1m);
            var costStressNetProfit = trial.Metrics.NetProfit - additionalCosts;

            metrics["costStressNetProfit"] = (double)costStressNetProfit;
            metrics["originalCommissions"] = (double)trial.Metrics.TotalCommissions;
            metrics["costStressMultiplier"] = thresholds.CostStressMultiplier;

            if (costStressNetProfit <= 0)
                failReason ??= "COST_STRESS_UNPROFITABLE";

            var passed = failReason is null;
            if (passed)
                survivors.Add(idx);

            verdicts.Add(new CandidateVerdict(trial.Id, passed, failReason, metrics));
        }

        return new StageResult(survivors, verdicts);
    }
}
