using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Genetic;

namespace AlgoTradeForge.Application.Optimization;

public sealed record EvaluateOptimizationQuery : IQuery<OptimizationEvaluationDto>
{
    public required string StrategyName { get; init; }
    public Dictionary<string, OptimizationAxisOverride>? Axes { get; init; }
    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public List<DataSubscriptionDto>? SubscriptionAxis { get; init; }
    public long MaxCombinations { get; init; } = 100_000;
    public required string Mode { get; init; } // "BruteForce" or "Genetic"
    public GeneticConfig? GeneticSettings { get; init; }
}

public sealed record OptimizationEvaluationDto
{
    public long TotalCombinations { get; init; }
    public bool ExceedsMaxCombinations { get; init; }
    public long MaxCombinations { get; init; }
    public int EffectiveDimensions { get; init; }
    public ResolvedGeneticConfigDto? GeneticConfig { get; init; }
}

public sealed record ResolvedGeneticConfigDto
{
    public int PopulationSize { get; init; }
    public int MaxGenerations { get; init; }
    public long MaxEvaluations { get; init; }
    public double MutationRate { get; init; }
}
