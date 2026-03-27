namespace AlgoTradeForge.Domain.Optimization.Fitness;

public sealed record FitnessConfig
{
    public FitnessWeights? Weights { get; init; }
    public int MinTrades { get; init; } = 10;
    public double MaxDrawdownThreshold { get; init; } = 30.0;
}
