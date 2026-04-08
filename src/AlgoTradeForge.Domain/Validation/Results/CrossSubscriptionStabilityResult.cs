namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Measures whether top-performing parameter regions overlap across subscription groups.
/// High stability score = same parameter region works across multiple time series (robustness).
/// </summary>
public sealed record CrossSubscriptionStabilityResult
{
    /// <summary>Stability score in [0, 1]. 1.0 = identical centroids across all groups.</summary>
    public required double StabilityScore { get; init; }

    /// <summary>Per-group cluster centroids (group key → parameter name → value).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> GroupCentroids { get; init; }

    /// <summary>Mean pairwise Euclidean distance between group centroids (in normalized space).</summary>
    public required double MeanCentroidDistance { get; init; }

    /// <summary>Number of subscription groups compared.</summary>
    public required int GroupCount { get; init; }
}
