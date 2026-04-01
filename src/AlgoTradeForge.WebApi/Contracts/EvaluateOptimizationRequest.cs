using System.Text.Json.Serialization;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Optimization;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record EvaluateOptimizationRequest
{
    public required string StrategyName { get; init; }

    [JsonConverter(typeof(OptimizationAxesConverter))]
    public Dictionary<string, OptimizationAxisOverride>? OptimizationAxes { get; init; }

    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public List<DataSubscriptionDto>? SubscriptionAxis { get; init; }
    public OptimizationSettingsInput? OptimizationSettings { get; init; }
    public string? Mode { get; init; } // "BruteForce" (default) | "Genetic"
    public GeneticSettingsInput? GeneticSettings { get; init; }
}

public sealed record OptimizationEvaluationResponse
{
    public long TotalCombinations { get; init; }
    public long? UniqueCombinations { get; init; }
    public bool ExceedsMaxCombinations { get; init; }
    public long MaxCombinations { get; init; }
    public int EffectiveDimensions { get; init; }
    public ResolvedGeneticConfigResponse? GeneticConfig { get; init; }
}

public sealed record ResolvedGeneticConfigResponse
{
    public int PopulationSize { get; init; }
    public int MaxGenerations { get; init; }
    public long MaxEvaluations { get; init; }
    public double MutationRate { get; init; }
}
