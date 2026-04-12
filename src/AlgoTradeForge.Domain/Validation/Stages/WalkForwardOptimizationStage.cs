namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 4: Walk-Forward Optimization. Validates whether the optimization process
/// generalizes across time by running rolling IS/OOS windows.
/// When multiple subscription groups exist, runs WFO per-group; the gate passes
/// only if ALL groups pass.
/// </summary>
public sealed class WalkForwardOptimizationStage : IValidationStage
{
    public int StageNumber => 4;
    public string StageName => "WalkForwardOptimization";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.WalkForwardOptimization;

        var config = new WfoConfig
        {
            WindowCount = thresholds.MinWfoRuns,
            OosPct = thresholds.OosPct,
            MinWfe = thresholds.MinWfe,
            MinProfitableWindowsPct = thresholds.MinProfitableWindowsPct,
            MaxOosDrawdownExcess = thresholds.MaxOosDrawdownExcess,
        };

        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        if (SubscriptionGroupHelper.IsSingleGroup(context.SubscriptionGroupByTrialIndex))
            return ExecuteSingleGroup(context, config, thresholds, initialEquity, ct);

        return ExecuteMultiGroup(context, config, thresholds, initialEquity, ct);
    }

    private static StageResult ExecuteSingleGroup(
        ValidationContext context, WfoConfig config,
        ValidationThresholdProfile.Stage4WalkForwardOptimizationThresholds thresholds,
        double initialEquity, CancellationToken ct)
    {
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        var wfoResult = WalkForwardEngine.RunWfo(context.Cache, config, initialEquity, ct);

        string? gateReason = DetermineGateReason(wfoResult, thresholds);
        var passed = gateReason is null;

        foreach (var idx in context.AllCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var metrics = BuildMetrics(wfoResult);

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

    private static StageResult ExecuteMultiGroup(
        ValidationContext context, WfoConfig config,
        ValidationThresholdProfile.Stage4WalkForwardOptimizationThresholds thresholds,
        double initialEquity, CancellationToken ct)
    {
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        var groups = SubscriptionGroupHelper.PartitionIndices(
            Enumerable.Range(0, context.Trials.Count).ToList(),
            context.SubscriptionGroupByTrialIndex);

        // Run WFO per subscription group
        var perGroupResults = new Dictionary<string, Results.WfoResult>();
        var perGroupGateReason = new Dictionary<string, string?>();
        foreach (var (groupKey, _) in groups)
        {
            ct.ThrowIfCancellationRequested();

            var allowedTrials = SubscriptionGroupHelper.GetTrialIndicesForGroup(
                context.SubscriptionGroupByTrialIndex, groupKey, context.Trials.Count);

            var result = WalkForwardEngine.RunWfo(
                context.Cache, config, initialEquity, ct, allowedTrials);
            perGroupResults[groupKey] = result;
            perGroupGateReason[groupKey] = DetermineGateReason(result, thresholds);
        }

        // Per-candidate gate: each candidate is judged by its own group's result
        foreach (var idx in context.AllCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var group = context.SubscriptionGroupByTrialIndex!.TryGetValue(idx, out var g) ? g : null;
            var groupResult = group is not null && perGroupResults.TryGetValue(group, out var gr) ? gr : null;
            var gateReason = group is not null && perGroupGateReason.TryGetValue(group, out var r) ? r : null;

            var metrics = groupResult is not null
                ? BuildMetrics(groupResult)
                : new Dictionary<string, double>();
            metrics["subscriptionGroupCount"] = perGroupResults.Count;

            if (gateReason is null && groupResult is not null)
            {
                survivors.Add(idx);
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, null, metrics));
            }
            else
            {
                var reason = gateReason is not null && group is not null
                    ? $"{gateReason} ({group})" : gateReason;
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, false, reason, metrics));
            }
        }

        return new StageResult(survivors, verdicts);
    }

    private static string? DetermineGateReason(
        Results.WfoResult result,
        ValidationThresholdProfile.Stage4WalkForwardOptimizationThresholds thresholds)
    {
        if (result.WalkForwardEfficiency < thresholds.MinWfe)
            return "WFE_BELOW_THRESHOLD";
        if (result.ProfitableWindowsPct < thresholds.MinProfitableWindowsPct)
            return "INSUFFICIENT_PROFITABLE_WINDOWS";
        if (result.MaxOosDrawdownExcessPct > thresholds.MaxOosDrawdownExcess)
            return "OOS_DRAWDOWN_EXCESSIVE";
        return null;
    }

    private static Dictionary<string, double> BuildMetrics(Results.WfoResult result) => new()
    {
        ["wfe"] = result.WalkForwardEfficiency,
        ["profitableWindowsPct"] = result.ProfitableWindowsPct,
        ["oosDrawdownExcessPct"] = result.MaxOosDrawdownExcessPct,
        ["windowCount"] = result.Windows.Count,
    };
}
