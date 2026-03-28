namespace AlgoTradeForge.Domain.Optimization.Fitness;

public sealed record FitnessConfig
{
    /// <summary>Canonical default config — single source of truth for backend fitness defaults.</summary>
    public static FitnessConfig Default { get; } = new();

    public FitnessWeights? Weights { get; init; }
    public int MinTrades { get; init; } = 10;
    public double MaxDrawdownThreshold { get; init; } = 30.0;
}
