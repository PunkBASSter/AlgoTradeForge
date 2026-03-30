namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Aggregate walk-forward optimization result across all windows.
/// </summary>
public sealed record WfoResult
{
    public required IReadOnlyList<WfoWindowResult> Windows { get; init; }
    public required double WalkForwardEfficiency { get; init; }
    public required double ProfitableWindowsPct { get; init; }
    public required double MaxOosDrawdownExcessPct { get; init; }
    public required bool Passed { get; init; }
}
