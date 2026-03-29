namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of Monte Carlo bootstrap simulation: drawdown percentiles,
/// equity fan bands for visualization, and probability of ruin.
/// </summary>
public sealed record MonteCarloResult
{
    /// <summary>Drawdown percentiles keyed by percentile (5, 25, 50, 75, 95). Values are max-DD percentages.</summary>
    public required IReadOnlyDictionary<int, double> DrawdownPercentiles { get; init; }

    /// <summary>
    /// Equity fan bands: 5 curves (one per percentile: 5th, 25th, 50th, 75th, 95th),
    /// each with one value per bar. Used for fan chart visualization.
    /// </summary>
    public required double[][] EquityFanBands { get; init; }

    /// <summary>Fraction of bootstrap iterations where equity dropped to zero or below.</summary>
    public required double ProbabilityOfRuin { get; init; }

    /// <summary>Number of bootstrap iterations performed.</summary>
    public required int Iterations { get; init; }
}
