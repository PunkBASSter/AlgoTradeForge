using AlgoTradeForge.Domain.Validation.Results;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Analyzes parameter sensitivity by examining how fitness degrades when parameters
/// are perturbed within a neighborhood of each candidate. Uses existing optimization
/// trial results as neighbors — no re-backtesting required.
/// </summary>
public static class ParameterSensitivityAnalyzer
{
    private const int HeatmapBins = 10;

    /// <summary>
    /// Analyze parameter sensitivity for the specified candidates.
    /// </summary>
    /// <param name="allTrials">All optimization trials with parameters and fitness.</param>
    /// <param name="candidateIndices">Indices of surviving candidates to evaluate.</param>
    /// <param name="sensitivityRange">±range for neighbor inclusion (e.g., 0.10 = ±10%).</param>
    /// <param name="maxDegradationPct">Maximum allowed fitness degradation (e.g., 0.30 = 30%).</param>
    public static ParameterSensitivityResult Analyze(
        IReadOnlyList<TrialSummary> allTrials,
        IReadOnlyList<int> candidateIndices,
        double sensitivityRange,
        double maxDegradationPct)
    {
        // Compute fitness for all trials
        var allFitness = new double[allTrials.Count];
        for (var i = 0; i < allTrials.Count; i++)
            allFitness[i] = ComputeTrialFitness(allTrials[i]);

        // Extract numeric param names from candidate trials only
        var paramNames = ExtractNumericParamNames(allTrials, candidateIndices);
        if (paramNames.Length == 0 || candidateIndices.Count == 0)
        {
            return new ParameterSensitivityResult
            {
                MeanFitnessRetention = 1.0,
                Heatmaps = [],
                PassedDegradationCheck = true,
            };
        }

        // Compute fitness retention for each candidate
        var retentions = new double[candidateIndices.Count];
        for (var ci = 0; ci < candidateIndices.Count; ci++)
        {
            var candidateIdx = candidateIndices[ci];
            var candidateFitness = allFitness[candidateIdx];
            if (candidateFitness <= 0)
            {
                retentions[ci] = 0;
                continue;
            }

            var candidateParams = allTrials[candidateIdx].Parameters!;
            var neighborFitnesses = new List<double>();

            for (var t = 0; t < allTrials.Count; t++)
            {
                if (t == candidateIdx) continue;
                if (allTrials[t].Parameters is null) continue;

                if (IsNeighbor(candidateParams, allTrials[t].Parameters!, paramNames, sensitivityRange))
                    neighborFitnesses.Add(allFitness[t]);
            }

            if (neighborFitnesses.Count == 0)
            {
                retentions[ci] = 1.0; // No neighbors = no degradation measurable
                continue;
            }

            var meanNeighborFitness = neighborFitnesses.Average();
            retentions[ci] = meanNeighborFitness / candidateFitness;
        }

        var meanRetention = retentions.Average();
        var passed = meanRetention >= (1.0 - maxDegradationPct);

        // Generate 2D heatmaps for each pair of numeric params
        var heatmaps = GenerateHeatmaps(allTrials, allFitness, paramNames, maxDegradationPct);

        return new ParameterSensitivityResult
        {
            MeanFitnessRetention = meanRetention,
            Heatmaps = heatmaps,
            PassedDegradationCheck = passed,
        };
    }

    private static string[] ExtractNumericParamNames(
        IReadOnlyList<TrialSummary> trials, IReadOnlyList<int> candidateIndices)
    {
        var names = new HashSet<string>();
        foreach (var idx in candidateIndices)
        {
            if (trials[idx].Parameters is null) continue;
            foreach (var (key, value) in trials[idx].Parameters!)
            {
                if (IsNumeric(value))
                    names.Add(key);
            }
        }

        return [.. names.Order()];
    }

    private static bool IsNeighbor(
        IReadOnlyDictionary<string, object> candidate,
        IReadOnlyDictionary<string, object> other,
        string[] paramNames,
        double range)
    {
        foreach (var name in paramNames)
        {
            if (!candidate.TryGetValue(name, out var cv) || !other.TryGetValue(name, out var ov))
                continue;
            if (!IsNumeric(cv) || !IsNumeric(ov))
                continue;

            var cVal = ToDouble(cv);
            var oVal = ToDouble(ov);
            var threshold = Math.Abs(cVal) * range;

            // For zero-valued params, use absolute range
            if (threshold < 1e-15) threshold = range;

            if (Math.Abs(oVal - cVal) > threshold)
                return false;
        }

        return true;
    }

    private static List<ParameterHeatmap> GenerateHeatmaps(
        IReadOnlyList<TrialSummary> trials,
        double[] fitness,
        string[] paramNames,
        double maxDegradationPct)
    {
        var heatmaps = new List<ParameterHeatmap>();
        if (paramNames.Length < 2) return heatmaps;

        // Collect param ranges
        var paramValues = new Dictionary<string, List<double>>();
        foreach (var name in paramNames)
            paramValues[name] = [];

        for (var t = 0; t < trials.Count; t++)
        {
            if (trials[t].Parameters is null) continue;
            foreach (var name in paramNames)
            {
                if (trials[t].Parameters!.TryGetValue(name, out var v) && IsNumeric(v))
                    paramValues[name].Add(ToDouble(v));
            }
        }

        // Generate heatmap for each pair
        for (var i = 0; i < paramNames.Length - 1; i++)
        {
            for (var j = i + 1; j < paramNames.Length; j++)
            {
                var heatmap = BuildHeatmap(
                    trials, fitness, paramNames[i], paramNames[j],
                    paramValues[paramNames[i]], paramValues[paramNames[j]],
                    maxDegradationPct);
                if (heatmap is not null)
                    heatmaps.Add(heatmap);
            }
        }

        return heatmaps;
    }

    private static ParameterHeatmap? BuildHeatmap(
        IReadOnlyList<TrialSummary> trials,
        double[] fitness,
        string param1, string param2,
        List<double> values1, List<double> values2,
        double maxDegradationPct)
    {
        if (values1.Count == 0 || values2.Count == 0) return null;

        var bins1 = ComputeBins(values1);
        var bins2 = ComputeBins(values2);
        if (bins1.Length < 2 || bins2.Length < 2) return null;

        var grid = new double[bins1.Length, bins2.Length];
        var counts = new int[bins1.Length, bins2.Length];

        for (var t = 0; t < trials.Count; t++)
        {
            if (trials[t].Parameters is null) continue;
            if (!trials[t].Parameters!.TryGetValue(param1, out var v1) ||
                !trials[t].Parameters!.TryGetValue(param2, out var v2))
                continue;
            if (!IsNumeric(v1) || !IsNumeric(v2)) continue;

            var bi = FindBin(bins1, ToDouble(v1));
            var bj = FindBin(bins2, ToDouble(v2));
            grid[bi, bj] += fitness[t];
            counts[bi, bj]++;
        }

        // Average fitness per bin
        var peak = double.MinValue;
        for (var i = 0; i < bins1.Length; i++)
        {
            for (var j = 0; j < bins2.Length; j++)
            {
                grid[i, j] = counts[i, j] > 0 ? grid[i, j] / counts[i, j] : double.NaN;
                if (!double.IsNaN(grid[i, j]) && grid[i, j] > peak)
                    peak = grid[i, j];
            }
        }

        // Plateau score: fraction of cells within threshold of peak
        var threshold = peak * (1.0 - maxDegradationPct);
        var totalCells = 0;
        var plateauCells = 0;
        for (var i = 0; i < bins1.Length; i++)
        {
            for (var j = 0; j < bins2.Length; j++)
            {
                if (double.IsNaN(grid[i, j])) continue;
                totalCells++;
                if (grid[i, j] >= threshold)
                    plateauCells++;
            }
        }

        var plateauScore = totalCells > 0 ? (double)plateauCells / totalCells : 0;

        return new ParameterHeatmap
        {
            Param1Name = param1,
            Param2Name = param2,
            Param1Values = bins1,
            Param2Values = bins2,
            FitnessGrid = grid,
            PlateauScore = plateauScore,
        };
    }

    private static double[] ComputeBins(List<double> values)
    {
        var distinct = values.Distinct().Order().ToArray();
        if (distinct.Length <= HeatmapBins)
            return distinct;

        // Quantile-based binning
        var bins = new double[HeatmapBins];
        for (var i = 0; i < HeatmapBins; i++)
        {
            var pct = (double)i / (HeatmapBins - 1);
            var idx = (int)(pct * (distinct.Length - 1));
            bins[i] = distinct[idx];
        }

        return bins.Distinct().ToArray();
    }

    private static int FindBin(double[] bins, double value)
    {
        var best = 0;
        var bestDist = Math.Abs(value - bins[0]);
        for (var i = 1; i < bins.Length; i++)
        {
            var dist = Math.Abs(value - bins[i]);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    private static double ComputeTrialFitness(TrialSummary trial) =>
        TrialFitnessEvaluator.Evaluate(trial.Metrics);

    private static bool IsNumeric(object value) => ParameterValueHelper.IsNumeric(value);

    private static double ToDouble(object value) => ParameterValueHelper.ToDouble(value);
}
