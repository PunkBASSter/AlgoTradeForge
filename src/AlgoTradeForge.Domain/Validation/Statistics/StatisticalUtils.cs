namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Shared statistical helper methods used across validation calculators.
/// </summary>
internal static class StatisticalUtils
{
    /// <summary>
    /// In-place Fisher-Yates shuffle of the given array using the provided RNG.
    /// </summary>
    internal static void FisherYatesShuffle(double[] array, Random rng)
    {
        for (var i = array.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }

    /// <summary>
    /// Floor-based percentile lookup on a pre-sorted array.
    /// Returns 0 for empty arrays.
    /// </summary>
    internal static double GetPercentile(double[] sorted, int percentile)
    {
        if (sorted.Length == 0) return 0.0;
        var index = (int)Math.Floor((percentile / 100.0) * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
