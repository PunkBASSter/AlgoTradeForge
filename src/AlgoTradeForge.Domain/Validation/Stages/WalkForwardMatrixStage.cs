namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 5: Walk-Forward Matrix. Stress-tests WFO across a grid of period counts
/// and OOS percentages. Requires a contiguous cluster of passing cells to validate
/// robustness to reoptimization frequency choices.
/// </summary>
public sealed class WalkForwardMatrixStage : IValidationStage
{
    public int StageNumber => 5;
    public string StageName => "WalkForwardMatrix";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.WalkForwardMatrix;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);

        var config = new WfmConfig
        {
            PeriodCounts = thresholds.PeriodCounts,
            OosPcts = thresholds.OosPcts,
            MinWfe = thresholds.MinWfe,
            MinContiguousRows = thresholds.MinContiguousRows,
            MinContiguousCols = thresholds.MinContiguousCols,
            MinCellsPassing = thresholds.MinCellsPassing,
            MinProfitableWindowsPct = thresholds.MinProfitableWindowsPct,
            MaxOosDrawdownExcess = thresholds.MaxOosDrawdownExcess,
        };

        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        var wfmResult = WalkForwardEngine.RunWfm(context.Cache, config, initialEquity, ct);

        // Gate checks
        string? gateReason = null;
        if (wfmResult.LargestContiguousCluster is null)
            gateReason = "WFM_NO_CONTIGUOUS_CLUSTER";
        else
        {
            var cluster = wfmResult.LargestContiguousCluster.Value;
            if (cluster.Rows < thresholds.MinContiguousRows ||
                cluster.Cols < thresholds.MinContiguousCols)
                gateReason = "WFM_CLUSTER_TOO_SMALL";
        }

        var passed = gateReason is null;

        foreach (var idx in context.ActiveCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var metrics = new Dictionary<string, double>
            {
                ["clusterPassCount"] = wfmResult.ClusterPassCount,
                ["totalCells"] = thresholds.PeriodCounts.Length * thresholds.OosPcts.Length,
                ["optimalReoptPeriod"] = wfmResult.OptimalReoptPeriod ?? 0,
            };

            if (wfmResult.LargestContiguousCluster is not null)
            {
                metrics["clusterRows"] = wfmResult.LargestContiguousCluster.Value.Rows;
                metrics["clusterCols"] = wfmResult.LargestContiguousCluster.Value.Cols;
            }

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
