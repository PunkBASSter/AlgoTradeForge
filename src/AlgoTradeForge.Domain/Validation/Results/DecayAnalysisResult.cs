namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of alpha decay analysis: rolling Sharpe time series
/// with linear regression slope to detect performance erosion over time.
/// </summary>
public sealed record DecayAnalysisResult
{
    /// <summary>Rolling Sharpe values at each bar position (after warmup window).</summary>
    public required IReadOnlyList<(int BarIndex, double Sharpe)> RollingSharpe { get; init; }

    /// <summary>Linear regression slope of rolling Sharpe vs bar index. Negative = decaying alpha.</summary>
    public required double SlopeCoefficient { get; init; }

    /// <summary>True if the slope is negative, indicating alpha erosion.</summary>
    public required bool IsDecaying { get; init; }
}
