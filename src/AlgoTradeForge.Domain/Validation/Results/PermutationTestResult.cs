namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of a permutation test measuring whether observed performance
/// is significantly better than random reorderings of the return sequence.
/// </summary>
public sealed record PermutationTestResult
{
    /// <summary>Fraction of permuted metrics that equal or exceed the observed metric. Low p-value = significant.</summary>
    public required double PValue { get; init; }

    /// <summary>The performance metric (e.g. Sharpe) computed on the original (unpermuted) sequence.</summary>
    public required double OriginalMetric { get; init; }

    /// <summary>Distribution of the metric across all permutations.</summary>
    public required double[] PermutedDistribution { get; init; }

    /// <summary>Number of permutation iterations performed.</summary>
    public required int Iterations { get; init; }

    /// <summary>
    /// Identifies the permutation variant: "PnlDelta" (current), "Price" or "Parameter" (future).
    /// </summary>
    public required string TestType { get; init; }
}
