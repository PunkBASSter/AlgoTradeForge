namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 5: Walk-Forward Matrix. Stress-tests WFO across a grid of period counts
/// and OOS percentages. Requires a contiguous cluster of passing cells.
/// When multiple subscription groups exist, runs WFM per-group; the gate passes
/// only if ALL groups find a sufficiently large contiguous cluster.
/// </summary>
public sealed class WalkForwardMatrixStage : IValidationStage
{
    public int StageNumber => 5;
    public string StageName => "WalkForwardMatrix";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.WalkForwardMatrix;

        var config = new WfmConfig
        {
            PeriodCounts = thresholds.PeriodCounts,
            OosPcts = thresholds.OosPcts,
            MinWfe = thresholds.MinWfe,
            MinContiguousRows = thresholds.MinContiguousRows,
            MinContiguousCols = thresholds.MinContiguousCols,
            MinCellsPassing = thresholds.MinCellsPassing,
            MinProfitableWindowsPct = context.Profile.WalkForwardOptimization.MinProfitableWindowsPct,
            MaxOosDrawdownExcess = context.Profile.WalkForwardOptimization.MaxOosDrawdownExcess,
        };

        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        if (SubscriptionGroupHelper.IsSingleGroup(context.SubscriptionGroupByTrialIndex))
            return ExecuteSingleGroup(context, config, thresholds, initialEquity, ct);

        return ExecuteMultiGroup(context, config, thresholds, initialEquity, ct);
    }

    private static StageResult ExecuteSingleGroup(
        ValidationContext context, WfmConfig config,
        ValidationThresholdProfile.Stage5WalkForwardMatrixThresholds thresholds,
        double initialEquity, CancellationToken ct)
    {
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        var wfmResult = WalkForwardEngine.RunWfm(context.Cache, config, initialEquity, ct);

        string? gateReason = DetermineGateReason(wfmResult, thresholds);
        var passed = gateReason is null;

        foreach (var idx in context.AllCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var metrics = BuildMetrics(wfmResult, thresholds);

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
        ValidationContext context, WfmConfig config,
        ValidationThresholdProfile.Stage5WalkForwardMatrixThresholds thresholds,
        double initialEquity, CancellationToken ct)
    {
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        var groups = SubscriptionGroupHelper.PartitionIndices(
            Enumerable.Range(0, context.Trials.Count).ToList(),
            context.SubscriptionGroupByTrialIndex);

        var perGroupResults = new Dictionary<string, Results.WfmResult>();
        var perGroupGateReason = new Dictionary<string, string?>();
        foreach (var (groupKey, _) in groups)
        {
            ct.ThrowIfCancellationRequested();

            var allowedTrials = SubscriptionGroupHelper.GetTrialIndicesForGroup(
                context.SubscriptionGroupByTrialIndex, groupKey, context.Trials.Count);

            var result = WalkForwardEngine.RunWfm(
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
                ? BuildMetrics(groupResult, thresholds)
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
        Results.WfmResult result,
        ValidationThresholdProfile.Stage5WalkForwardMatrixThresholds thresholds)
    {
        if (result.LargestContiguousCluster is null)
            return "WFM_NO_CONTIGUOUS_CLUSTER";

        var cluster = result.LargestContiguousCluster.Value;
        if (cluster.Rows < thresholds.MinContiguousRows ||
            cluster.Cols < thresholds.MinContiguousCols)
            return "WFM_CLUSTER_TOO_SMALL";

        return null;
    }

    private static Dictionary<string, double> BuildMetrics(
        Results.WfmResult result,
        ValidationThresholdProfile.Stage5WalkForwardMatrixThresholds thresholds)
    {
        var metrics = new Dictionary<string, double>
        {
            ["clusterPassCount"] = result.ClusterPassCount,
            ["totalCells"] = thresholds.PeriodCounts.Length * thresholds.OosPcts.Length,
            ["optimalReoptPeriod"] = result.OptimalReoptPeriod ?? 0,
        };

        if (result.LargestContiguousCluster is not null)
        {
            metrics["clusterRows"] = result.LargestContiguousCluster.Value.Rows;
            metrics["clusterCols"] = result.LargestContiguousCluster.Value.Cols;
        }

        return metrics;
    }
}
