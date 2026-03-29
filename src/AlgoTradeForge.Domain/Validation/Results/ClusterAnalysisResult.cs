namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of K-Means clustering on top-performing parameter sets.
/// High primary cluster concentration indicates parameters converge to a stable region.
/// </summary>
public sealed record ClusterAnalysisResult
{
    public required double PrimaryClusterConcentration { get; init; }
    public required int ClusterCount { get; init; }
    public required IReadOnlyDictionary<string, double> ClusterCentroid { get; init; }
    public required double SilhouetteScore { get; init; }
}
