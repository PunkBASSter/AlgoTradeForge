namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of sub-period consistency analysis: equity curve smoothness,
/// performance stability across equal time slices, and R² fit.
/// </summary>
public sealed record SubPeriodConsistencyResult
{
    /// <summary>Fraction of sub-periods with positive total return [0, 1].</summary>
    public required double ProfitableSubPeriodsPct { get; init; }

    /// <summary>Coefficient of variation of Sharpe ratios across sub-periods. Lower = more consistent.</summary>
    public required double SharpeCoeffOfVariation { get; init; }

    /// <summary>R² of linear regression on cumulative equity vs bar index. 1.0 = perfectly linear growth.</summary>
    public required double EquityCurveR2 { get; init; }

    /// <summary>Per-sub-period performance metrics.</summary>
    public required IReadOnlyList<SubPeriodMetrics> SubPeriods { get; init; }
}

/// <summary>Performance metrics for a single sub-period.</summary>
public sealed record SubPeriodMetrics
{
    /// <summary>First bar index (inclusive) of this sub-period.</summary>
    public required int StartBar { get; init; }

    /// <summary>Last bar index (exclusive) of this sub-period.</summary>
    public required int EndBar { get; init; }

    /// <summary>Annualized Sharpe ratio for this sub-period.</summary>
    public required double Sharpe { get; init; }

    /// <summary>Total return percentage for this sub-period.</summary>
    public required double ReturnPct { get; init; }

    /// <summary>Profit factor (gross profit / gross loss) for this sub-period.</summary>
    public required double ProfitFactor { get; init; }
}
