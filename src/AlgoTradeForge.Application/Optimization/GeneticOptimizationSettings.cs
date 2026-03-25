namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// User-facing settings for genetic optimization, mapped from API input.
/// Zero values trigger auto-sizing via GeneticConfigResolver.
/// </summary>
public sealed record GeneticOptimizationSettings
{
    public int PopulationSize { get; init; }
    public int MaxGenerations { get; init; }
    public long MaxEvaluations { get; init; }
    public int EliteCount { get; init; } = 2;
    public double CrossoverRate { get; init; } = 0.85;
    public int TournamentSize { get; init; } = 3;
    public int StagnationLimit { get; init; } = 20;
    public TimeSpan? TimeBudget { get; init; }

    // Fitness weights
    public double SharpeWeight { get; init; } = 0.5;
    public double SortinoWeight { get; init; } = 0.2;
    public double ProfitFactorWeight { get; init; } = 0.15;
    public double AnnualizedReturnWeight { get; init; } = 0.15;
    public double MaxDrawdownThreshold { get; init; } = 30.0;
    public int MinTrades { get; init; } = 10;
}
