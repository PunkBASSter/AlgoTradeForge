namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of regime detection and per-regime performance analysis.
/// Regimes are classified by rolling volatility percentiles (Bull/Bear/Sideways).
/// </summary>
public sealed record RegimeAnalysisResult
{
    /// <summary>Detected regime segments in chronological order.</summary>
    public required IReadOnlyList<RegimeSegment> Regimes { get; init; }

    /// <summary>Number of distinct regime segments with positive returns.</summary>
    public required int ProfitableRegimeCount { get; init; }

    /// <summary>Min and max Sharpe across all regime segments.</summary>
    public required (double Min, double Max) SharpeRange { get; init; }
}

/// <summary>A contiguous time segment classified as a single regime.</summary>
public sealed record RegimeSegment
{
    /// <summary>Regime label: "Bull", "Bear", or "Sideways".</summary>
    public required string Label { get; init; }

    /// <summary>First bar index (inclusive) of this regime segment.</summary>
    public required int StartBar { get; init; }

    /// <summary>Last bar index (exclusive) of this regime segment.</summary>
    public required int EndBar { get; init; }

    /// <summary>Annualized Sharpe ratio for bars within this segment.</summary>
    public required double Sharpe { get; init; }

    /// <summary>Total return percentage for bars within this segment.</summary>
    public required double ReturnPct { get; init; }

    /// <summary>Maximum drawdown percentage for bars within this segment.</summary>
    public required double MaxDrawdownPct { get; init; }
}
