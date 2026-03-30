using AlgoTradeForge.Domain.Validation.Statistics;

namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 3: Parameter landscape analysis. Examines clustering of top-performing
/// parameter sets and sensitivity of each candidate's fitness to parameter perturbation.
/// </summary>
public sealed class ParameterLandscapeStage : IValidationStage
{
    public int StageNumber => 3;
    public string StageName => "ParameterLandscape";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.ParameterLandscape;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);

        // Check if any trial has parameters
        var hasParameters = context.Trials.Any(t => t.Parameters is not null && t.Parameters.Count > 0);
        if (!hasParameters)
        {
            // Skip stage — pass all candidates through with NO_PARAMETERS reason
            foreach (var idx in context.ActiveCandidateIndices)
            {
                survivors.Add(idx);
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "NO_PARAMETERS", []));
            }

            return new StageResult(survivors, verdicts);
        }

        // Cluster analysis on all active trials
        var activeTrials = context.ActiveCandidateIndices
            .Where(i => context.Trials[i].Parameters is not null)
            .ToList();

        var paramSets = activeTrials
            .Select(i => context.Trials[i].Parameters!)
            .ToList();

        var fitnessScores = activeTrials
            .Select(i => ComputeFitness(context.Trials[i]))
            .ToList();

        var clusterResult = ClusterAnalyzer.Analyze(paramSets, fitnessScores);

        // If cluster concentration is too low, reject all candidates
        if (clusterResult.PrimaryClusterConcentration < thresholds.MinClusterConcentration)
        {
            foreach (var idx in context.ActiveCandidateIndices)
            {
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, false,
                    "CLUSTER_CONCENTRATION_LOW",
                    new Dictionary<string, double>
                    {
                        ["primaryClusterConcentration"] = clusterResult.PrimaryClusterConcentration,
                        ["clusterCount"] = clusterResult.ClusterCount,
                        ["silhouetteScore"] = clusterResult.SilhouetteScore,
                    }));
            }

            return new StageResult(survivors, verdicts);
        }

        // Sensitivity analysis for active candidates
        var sensitivityResult = ParameterSensitivityAnalyzer.Analyze(
            context.Trials, context.ActiveCandidateIndices,
            thresholds.SensitivityRange, thresholds.MaxDegradationPct);

        // Per-candidate verdicts
        foreach (var idx in context.ActiveCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var metrics = new Dictionary<string, double>
            {
                ["meanFitnessRetention"] = sensitivityResult.MeanFitnessRetention,
                ["primaryClusterConcentration"] = clusterResult.PrimaryClusterConcentration,
                ["silhouetteScore"] = clusterResult.SilhouetteScore,
                ["clusterCount"] = clusterResult.ClusterCount,
            };

            if (!sensitivityResult.PassedDegradationCheck)
            {
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, false,
                    "PARAMETER_SENSITIVITY_EXCESSIVE", metrics));
            }
            else
            {
                survivors.Add(idx);
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, null, metrics));
            }
        }

        return new StageResult(survivors, verdicts);
    }

    private static double ComputeFitness(TrialSummary trial) =>
        TrialFitnessEvaluator.Evaluate(trial.Metrics);
}
