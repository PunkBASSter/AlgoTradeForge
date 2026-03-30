using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// K-Means clustering on top-performing parameter sets to detect whether
/// optimal parameters converge to a stable region (high concentration) or
/// scatter randomly (sign of overfitting).
/// </summary>
public static class ClusterAnalyzer
{
    private const int MaxIterations = 100;

    /// <summary>
    /// Analyze parameter clustering among top-performing trials.
    /// </summary>
    /// <param name="parameterSets">Each trial's parameter dictionary.</param>
    /// <param name="fitnessScores">Fitness score per trial (same order).</param>
    /// <param name="topPercentile">Fraction of top trials to cluster (default 0.25 = top 25%).</param>
    /// <param name="maxClusters">Maximum k to test (default 5).</param>
    public static ClusterAnalysisResult Analyze(
        IReadOnlyList<IReadOnlyDictionary<string, object>> parameterSets,
        IReadOnlyList<double> fitnessScores,
        double topPercentile = 0.25,
        int maxClusters = 5)
    {
        // Filter to top percentile by fitness
        var indexed = fitnessScores
            .Select((f, i) => (Index: i, Fitness: f))
            .OrderByDescending(x => x.Fitness)
            .ToArray();

        var topN = Math.Max(2, (int)(indexed.Length * topPercentile));
        var topIndices = indexed.Take(topN).Select(x => x.Index).ToArray();

        // Extract numeric param names
        var paramNames = ExtractNumericParamNames(parameterSets, topIndices);
        if (paramNames.Length == 0)
        {
            return new ClusterAnalysisResult
            {
                PrimaryClusterConcentration = 1.0,
                ClusterCount = 1,
                ClusterCentroid = new Dictionary<string, double>(),
                SilhouetteScore = 0,
            };
        }

        // Build normalized data matrix: topN × dimensions
        var (data, mins, ranges) = NormalizeData(parameterSets, topIndices, paramNames);

        // Try k=1..maxClusters, pick best silhouette
        var bestK = 1;
        var bestSilhouette = double.MinValue;
        int[]? bestAssignments = null;
        double[][]? bestCentroids = null;

        // k=1: no silhouette, just baseline
        var assignments1 = new int[topN];
        var centroids1 = new[] { ComputeCentroid(data, assignments1, 0) };
        bestAssignments = assignments1;
        bestCentroids = centroids1;
        bestSilhouette = 0;

        for (var k = 2; k <= Math.Min(maxClusters, topN); k++)
        {
            var (assignments, centroids) = RunKMeans(data, k);
            var silhouette = ComputeSilhouette(data, assignments, k);

            if (silhouette > bestSilhouette)
            {
                bestK = k;
                bestSilhouette = silhouette;
                bestAssignments = assignments;
                bestCentroids = centroids;
            }
        }

        // Find primary cluster (largest)
        var clusterSizes = new int[bestK];
        for (var i = 0; i < bestAssignments!.Length; i++)
            clusterSizes[bestAssignments[i]]++;

        var primaryCluster = 0;
        for (var c = 1; c < bestK; c++)
            if (clusterSizes[c] > clusterSizes[primaryCluster])
                primaryCluster = c;

        var concentration = (double)clusterSizes[primaryCluster] / topN;

        // Denormalize centroid to original scale
        var centroidDict = new Dictionary<string, double>();
        for (var d = 0; d < paramNames.Length; d++)
        {
            var normalizedVal = bestCentroids![primaryCluster][d];
            centroidDict[paramNames[d]] = ranges[d] > 0
                ? mins[d] + normalizedVal * ranges[d]
                : mins[d];
        }

        return new ClusterAnalysisResult
        {
            PrimaryClusterConcentration = concentration,
            ClusterCount = bestK,
            ClusterCentroid = centroidDict,
            SilhouetteScore = bestK == 1 ? 0 : bestSilhouette,
        };
    }

    private static string[] ExtractNumericParamNames(
        IReadOnlyList<IReadOnlyDictionary<string, object>> paramSets, int[] indices)
    {
        var names = new HashSet<string>();
        foreach (var idx in indices)
        {
            foreach (var (key, value) in paramSets[idx])
            {
                if (IsNumeric(value))
                    names.Add(key);
            }
        }

        return [.. names.Order()];
    }

    private static (double[][] Data, double[] Mins, double[] Ranges) NormalizeData(
        IReadOnlyList<IReadOnlyDictionary<string, object>> paramSets,
        int[] indices, string[] paramNames)
    {
        var dims = paramNames.Length;
        var n = indices.Length;
        var raw = new double[n][];

        for (var i = 0; i < n; i++)
        {
            raw[i] = new double[dims];
            var ps = paramSets[indices[i]];
            for (var d = 0; d < dims; d++)
                raw[i][d] = ps.TryGetValue(paramNames[d], out var v) ? ToDouble(v) : 0;
        }

        var mins = new double[dims];
        var maxs = new double[dims];
        for (var d = 0; d < dims; d++)
        {
            mins[d] = double.MaxValue;
            maxs[d] = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                if (raw[i][d] < mins[d]) mins[d] = raw[i][d];
                if (raw[i][d] > maxs[d]) maxs[d] = raw[i][d];
            }
        }

        var ranges = new double[dims];
        for (var d = 0; d < dims; d++)
            ranges[d] = maxs[d] - mins[d];

        // Normalize to [0,1]
        var data = new double[n][];
        for (var i = 0; i < n; i++)
        {
            data[i] = new double[dims];
            for (var d = 0; d < dims; d++)
                data[i][d] = ranges[d] > 0 ? (raw[i][d] - mins[d]) / ranges[d] : 0;
        }

        return (data, mins, ranges);
    }

    private static (int[] Assignments, double[][] Centroids) RunKMeans(double[][] data, int k)
    {
        var n = data.Length;
        var dims = data[0].Length;

        // K-Means++ initialization
        var centroids = KMeansPlusPlusInit(data, k);
        var assignments = new int[n];

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            // Assignment step
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                var nearest = FindNearest(data[i], centroids);
                if (nearest != assignments[i])
                {
                    assignments[i] = nearest;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update step
            for (var c = 0; c < k; c++)
                centroids[c] = ComputeCentroid(data, assignments, c);
        }

        return (assignments, centroids);
    }

    private static double[][] KMeansPlusPlusInit(double[][] data, int k)
    {
        var rng = new Random(42); // Deterministic for reproducibility
        var n = data.Length;
        var dims = data[0].Length;
        var centroids = new double[k][];

        // First centroid: random point
        centroids[0] = (double[])data[rng.Next(n)].Clone();

        var distances = new double[n];
        for (var c = 1; c < k; c++)
        {
            // Compute min distance to existing centroids
            var totalDist = 0.0;
            for (var i = 0; i < n; i++)
            {
                var minDist = double.MaxValue;
                for (var j = 0; j < c; j++)
                {
                    var d = EuclideanDistanceSq(data[i], centroids[j]);
                    if (d < minDist) minDist = d;
                }

                distances[i] = minDist;
                totalDist += minDist;
            }

            // Weighted random selection
            if (totalDist <= 0)
            {
                centroids[c] = (double[])data[rng.Next(n)].Clone();
                continue;
            }

            var threshold = rng.NextDouble() * totalDist;
            var cumulative = 0.0;
            for (var i = 0; i < n; i++)
            {
                cumulative += distances[i];
                if (cumulative >= threshold)
                {
                    centroids[c] = (double[])data[i].Clone();
                    break;
                }
            }

            centroids[c] ??= (double[])data[n - 1].Clone();
        }

        return centroids;
    }

    private static double[] ComputeCentroid(double[][] data, int[] assignments, int cluster)
    {
        var dims = data[0].Length;
        var centroid = new double[dims];
        var count = 0;

        for (var i = 0; i < data.Length; i++)
        {
            if (assignments[i] != cluster) continue;
            count++;
            for (var d = 0; d < dims; d++)
                centroid[d] += data[i][d];
        }

        if (count > 0)
            for (var d = 0; d < dims; d++)
                centroid[d] /= count;

        return centroid;
    }

    private static int FindNearest(double[] point, double[][] centroids)
    {
        var best = 0;
        var bestDist = EuclideanDistanceSq(point, centroids[0]);
        for (var c = 1; c < centroids.Length; c++)
        {
            var d = EuclideanDistanceSq(point, centroids[c]);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }

        return best;
    }

    private static double ComputeSilhouette(double[][] data, int[] assignments, int k)
    {
        var n = data.Length;
        if (n <= 1) return 0;

        var totalSilhouette = 0.0;
        for (var i = 0; i < n; i++)
        {
            var myCluster = assignments[i];

            // a(i) = mean distance to same-cluster points
            var sameSum = 0.0;
            var sameCount = 0;
            for (var j = 0; j < n; j++)
            {
                if (j == i || assignments[j] != myCluster) continue;
                sameSum += Math.Sqrt(EuclideanDistanceSq(data[i], data[j]));
                sameCount++;
            }

            var a = sameCount > 0 ? sameSum / sameCount : 0;

            // b(i) = min mean distance to other-cluster points
            var b = double.MaxValue;
            for (var c = 0; c < k; c++)
            {
                if (c == myCluster) continue;
                var otherSum = 0.0;
                var otherCount = 0;
                for (var j = 0; j < n; j++)
                {
                    if (assignments[j] != c) continue;
                    otherSum += Math.Sqrt(EuclideanDistanceSq(data[i], data[j]));
                    otherCount++;
                }

                if (otherCount > 0)
                {
                    var meanDist = otherSum / otherCount;
                    if (meanDist < b) b = meanDist;
                }
            }

            if (b == double.MaxValue) b = 0;
            var denom = Math.Max(a, b);
            totalSilhouette += denom > 0 ? (b - a) / denom : 0;
        }

        return totalSilhouette / n;
    }

    private static double EuclideanDistanceSq(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var d = 0; d < a.Length; d++)
        {
            var diff = a[d] - b[d];
            sum += diff * diff;
        }

        return sum;
    }

    private static bool IsNumeric(object value) => ParameterValueHelper.IsNumeric(value);

    private static double ToDouble(object value) => ParameterValueHelper.ToDouble(value);
}
