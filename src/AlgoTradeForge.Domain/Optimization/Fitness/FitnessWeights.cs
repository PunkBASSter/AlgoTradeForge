namespace AlgoTradeForge.Domain.Optimization.Fitness;

/// <summary>
/// Weights for the composite fitness function.
/// </summary>
public sealed record FitnessWeights
{
    public double SharpeWeight { get; init; } = 0.5;
    public double SortinoWeight { get; init; } = 0.2;
    public double ProfitFactorWeight { get; init; } = 0.15;
    public double AnnualizedReturnWeight { get; init; } = 0.15;
}
