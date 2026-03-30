namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of Combinatorially Symmetric Cross-Validation (CSCV),
/// producing the Probability of Backtest Overfitting (PBO).
/// </summary>
public sealed record PboResult
{
    /// <summary>
    /// Probability of Backtest Overfitting [0, 1]. Fraction of IS/OOS splits where
    /// the IS-optimal trial ranks below the OOS median. PBO &lt; 0.30 is conservative target.
    /// </summary>
    public required double Pbo { get; init; }

    /// <summary>Logit of the IS-optimal trial's OOS rank for each CSCV combination.</summary>
    public required double[] LogitDistribution { get; init; }

    /// <summary>Total number of C(S, S/2) combinations evaluated.</summary>
    public required int NumCombinations { get; init; }

    /// <summary>Number of blocks S used for partitioning.</summary>
    public required int NumBlocks { get; init; }
}
