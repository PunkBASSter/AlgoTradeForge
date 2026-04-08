namespace AlgoTradeForge.Domain.Validation.Stages;

public sealed class ValidationContext
{
    public required SimulationCache Cache { get; init; }
    public required IReadOnlyList<TrialSummary> Trials { get; init; }
    public required ValidationThresholdProfile Profile { get; init; }
    public required IReadOnlyList<int> AllCandidateIndices { get; init; }

    /// <summary>
    /// Total parameter combinations tested during the source optimization run.
    /// Used by Stage 0 (PreFlight) for MinBTL calculation.
    /// </summary>
    public long TotalCombinations { get; init; }

    /// <summary>
    /// Maps trial index → subscription group key (e.g., "BTCUSDT:binance:1h").
    /// Null when single-subscription optimization (stages skip partitioning).
    /// </summary>
    public IReadOnlyDictionary<int, string>? SubscriptionGroupByTrialIndex { get; init; }
}
