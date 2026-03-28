using System.Text.Json.Serialization;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record GeneticSettingsInput
{
    public int PopulationSize { get; init; }
    public int MaxGenerations { get; init; }
    public long MaxEvaluations { get; init; }
    public int EliteCount { get; init; } = 2;
    public double CrossoverRate { get; init; } = 0.85;
    public int TournamentSize { get; init; } = 3;
    public int StagnationLimit { get; init; } = 20;
    public int? TimeBudgetMinutes { get; init; }
}

public sealed record RunGeneticOptimizationRequest
{
    public required string StrategyName { get; init; }
    public required BacktestSettingsInput BacktestSettings { get; init; }
    public OptimizationSettingsInput OptimizationSettings { get; init; } = new();
    public GeneticSettingsInput GeneticSettings { get; init; } = new();

    [JsonConverter(typeof(OptimizationAxesConverter))]
    public Dictionary<string, OptimizationAxisOverride>? OptimizationAxes { get; init; }

    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public List<DataSubscriptionDto>? SubscriptionAxis { get; init; }
}
