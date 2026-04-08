using AlgoTradeForge.Domain.Validation.Results;
using AlgoTradeForge.Domain.Validation.Statistics;

namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 3: Parameter landscape analysis. Examines clustering of top-performing
/// parameter sets and sensitivity of each candidate's fitness to parameter perturbation.
/// When multiple subscription groups exist, runs analysis per-group and computes
/// cross-subscription stability (centroid overlap across groups).
/// </summary>
public sealed class ParameterLandscapeStage : IValidationStage
{
    public int StageNumber => 3;
    public string StageName => "ParameterLandscape";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.ParameterLandscape;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        // Check if any trial has parameters
        var hasParameters = context.Trials.Any(t => t.Parameters is not null && t.Parameters.Count > 0);
        if (!hasParameters)
        {
            foreach (var idx in context.AllCandidateIndices)
            {
                survivors.Add(idx);
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, "NO_PARAMETERS", []));
            }

            return new StageResult(survivors, verdicts);
        }

        // Single-group fast-path: identical to previous behavior
        if (SubscriptionGroupHelper.IsSingleGroup(context.SubscriptionGroupByTrialIndex))
            return ExecuteSingleGroup(context, thresholds, ct);

        return ExecuteMultiGroup(context, thresholds, ct);
    }

    private static StageResult ExecuteSingleGroup(
        ValidationContext context,
        ValidationThresholdProfile.Stage3ParameterLandscapeThresholds thresholds,
        CancellationToken ct)
    {
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        var activeTrials = context.AllCandidateIndices
            .Where(i => context.Trials[i].Parameters is not null)
            .ToList();

        var paramSets = activeTrials
            .Select(i => context.Trials[i].Parameters!)
            .ToList();

        var fitnessScores = activeTrials
            .Select(i => ComputeFitness(context.Trials[i]))
            .ToList();

        var clusterResult = ClusterAnalyzer.Analyze(paramSets, fitnessScores);

        if (clusterResult.PrimaryClusterConcentration < thresholds.MinClusterConcentration)
        {
            foreach (var idx in context.AllCandidateIndices)
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

        var sensitivityResult = ParameterSensitivityAnalyzer.Analyze(
            context.Trials, context.AllCandidateIndices,
            thresholds.SensitivityRange, thresholds.MaxDegradationPct);

        foreach (var idx in context.AllCandidateIndices)
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

    private static StageResult ExecuteMultiGroup(
        ValidationContext context,
        ValidationThresholdProfile.Stage3ParameterLandscapeThresholds thresholds,
        CancellationToken ct)
    {
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        // Partition all candidate indices by subscription group
        var groups = SubscriptionGroupHelper.PartitionIndices(
            context.AllCandidateIndices, context.SubscriptionGroupByTrialIndex);

        // Run cluster analysis per group
        var perGroupCluster = new Dictionary<string, ClusterAnalysisResult>();
        var perGroupSensitivity = new Dictionary<string, ParameterSensitivityResult>();

        foreach (var (groupKey, groupIndices) in groups)
        {
            ct.ThrowIfCancellationRequested();

            var activeInGroup = groupIndices
                .Where(i => context.Trials[i].Parameters is not null)
                .ToList();

            if (activeInGroup.Count == 0) continue;

            var paramSets = activeInGroup
                .Select(i => context.Trials[i].Parameters!)
                .ToList();

            var fitnessScores = activeInGroup
                .Select(i => ComputeFitness(context.Trials[i]))
                .ToList();

            perGroupCluster[groupKey] = ClusterAnalyzer.Analyze(paramSets, fitnessScores);

            // Sensitivity analysis within the group: only consider group trials as neighbors
            perGroupSensitivity[groupKey] = ParameterSensitivityAnalyzer.Analyze(
                // Pass all trials but restrict candidates to this group
                context.Trials, activeInGroup,
                thresholds.SensitivityRange, thresholds.MaxDegradationPct);
        }

        // Compute cross-subscription stability from group centroids
        var stabilityResult = ComputeCrossSubscriptionStability(perGroupCluster);

        // Check cross-subscription stability gate
        var crossSubFailed = stabilityResult.GroupCount > 1
            && stabilityResult.StabilityScore < thresholds.MinCrossSubscriptionStability;

        // Build a lookup: trial index → group key
        var trialGroupLookup = new Dictionary<int, string>();
        foreach (var (groupKey, groupIndices) in groups)
        {
            foreach (var idx in groupIndices)
                trialGroupLookup[idx] = groupKey;
        }

        // Per-candidate verdicts: must pass in its group AND cross-sub stability
        foreach (var idx in context.AllCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            if (!trialGroupLookup.TryGetValue(idx, out var group))
            {
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, false,
                    "NO_SUBSCRIPTION_GROUP", []));
                continue;
            }

            // Get this candidate's group results (may not exist if group had no active trials)
            var hasGroupCluster = perGroupCluster.TryGetValue(group, out var groupCluster);
            var hasGroupSensitivity = perGroupSensitivity.TryGetValue(group, out var groupSensitivity);

            // Worst-case metrics across all groups for reporting
            var worstConcentration = perGroupCluster.Count > 0
                ? perGroupCluster.Values.Min(c => c.PrimaryClusterConcentration)
                : 0.0;
            var worstRetention = perGroupSensitivity.Count > 0
                ? perGroupSensitivity.Values.Min(s => s.MeanFitnessRetention)
                : 0.0;

            var metrics = new Dictionary<string, double>
            {
                ["primaryClusterConcentration"] = hasGroupCluster
                    ? groupCluster!.PrimaryClusterConcentration : 0.0,
                ["clusterCount"] = hasGroupCluster ? groupCluster!.ClusterCount : 0,
                ["silhouetteScore"] = hasGroupCluster ? groupCluster!.SilhouetteScore : 0.0,
                ["meanFitnessRetention"] = hasGroupSensitivity
                    ? groupSensitivity!.MeanFitnessRetention : 0.0,
                ["crossSubscriptionStability"] = stabilityResult.StabilityScore,
                ["subscriptionGroupCount"] = stabilityResult.GroupCount,
                ["worstGroupConcentration"] = worstConcentration,
                ["worstGroupRetention"] = worstRetention,
            };

            // Determine pass/fail
            string? reason = null;

            if (crossSubFailed)
                reason = "CROSS_SUBSCRIPTION_STABILITY_LOW";
            else if (hasGroupCluster
                     && groupCluster!.PrimaryClusterConcentration < thresholds.MinClusterConcentration)
                reason = "CLUSTER_CONCENTRATION_LOW";
            else if (hasGroupSensitivity && !groupSensitivity!.PassedDegradationCheck)
                reason = "PARAMETER_SENSITIVITY_EXCESSIVE";

            if (reason is null)
            {
                survivors.Add(idx);
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, true, null, metrics));
            }
            else
            {
                verdicts.Add(new CandidateVerdict(context.Trials[idx].Id, false, reason, metrics));
            }
        }

        return new StageResult(survivors, verdicts);
    }

    /// <summary>
    /// Computes cross-subscription stability by comparing cluster centroids across groups.
    /// Normalizes each centroid into a common [0,1] space and measures pairwise proximity.
    /// </summary>
    internal static CrossSubscriptionStabilityResult ComputeCrossSubscriptionStability(
        IReadOnlyDictionary<string, ClusterAnalysisResult> perGroupResults)
    {
        var groupCentroids = perGroupResults
            .Where(kv => kv.Value.ClusterCentroid.Count > 0)
            .ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, double>)kv.Value.ClusterCentroid);

        if (groupCentroids.Count <= 1)
        {
            return new CrossSubscriptionStabilityResult
            {
                StabilityScore = 1.0,
                GroupCentroids = groupCentroids,
                MeanCentroidDistance = 0.0,
                GroupCount = groupCentroids.Count,
            };
        }

        // Collect all parameter names across all group centroids
        var allParamNames = new HashSet<string>();
        foreach (var centroid in groupCentroids.Values)
        {
            foreach (var key in centroid.Keys)
                allParamNames.Add(key);
        }

        var paramNames = allParamNames.OrderBy(n => n).ToArray();
        if (paramNames.Length == 0)
        {
            return new CrossSubscriptionStabilityResult
            {
                StabilityScore = 1.0,
                GroupCentroids = groupCentroids,
                MeanCentroidDistance = 0.0,
                GroupCount = groupCentroids.Count,
            };
        }

        // Extract raw centroid vectors
        var groupKeys = groupCentroids.Keys.ToArray();
        var vectors = new double[groupKeys.Length][];
        for (var g = 0; g < groupKeys.Length; g++)
        {
            vectors[g] = new double[paramNames.Length];
            var centroid = groupCentroids[groupKeys[g]];
            for (var p = 0; p < paramNames.Length; p++)
                vectors[g][p] = centroid.TryGetValue(paramNames[p], out var v) ? v : 0.0;
        }

        // Normalize to [0,1] per parameter dimension
        var mins = new double[paramNames.Length];
        var ranges = new double[paramNames.Length];
        for (var p = 0; p < paramNames.Length; p++)
        {
            var min = double.MaxValue;
            var max = double.MinValue;
            for (var g = 0; g < vectors.Length; g++)
            {
                if (vectors[g][p] < min) min = vectors[g][p];
                if (vectors[g][p] > max) max = vectors[g][p];
            }

            mins[p] = min;
            ranges[p] = max - min;
        }

        var normalized = new double[vectors.Length][];
        for (var g = 0; g < vectors.Length; g++)
        {
            normalized[g] = new double[paramNames.Length];
            for (var p = 0; p < paramNames.Length; p++)
                normalized[g][p] = ranges[p] > 0 ? (vectors[g][p] - mins[p]) / ranges[p] : 0.0;
        }

        // Compute pairwise Euclidean distances
        var pairCount = 0;
        var totalDistance = 0.0;
        for (var i = 0; i < normalized.Length - 1; i++)
        {
            for (var j = i + 1; j < normalized.Length; j++)
            {
                var distSq = 0.0;
                for (var p = 0; p < paramNames.Length; p++)
                {
                    var diff = normalized[i][p] - normalized[j][p];
                    distSq += diff * diff;
                }

                totalDistance += Math.Sqrt(distSq);
                pairCount++;
            }
        }

        var meanDistance = pairCount > 0 ? totalDistance / pairCount : 0.0;

        // Max possible distance in normalized space = sqrt(dimensions)
        var maxDistance = Math.Sqrt(paramNames.Length);
        var stabilityScore = maxDistance > 0
            ? Math.Clamp(1.0 - meanDistance / maxDistance, 0.0, 1.0)
            : 1.0;

        return new CrossSubscriptionStabilityResult
        {
            StabilityScore = stabilityScore,
            GroupCentroids = groupCentroids,
            MeanCentroidDistance = meanDistance,
            GroupCount = groupCentroids.Count,
        };
    }

    private static double ComputeFitness(TrialSummary trial) =>
        TrialFitnessEvaluator.Evaluate(trial.Metrics);
}
