using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Optimization;

public sealed record RunGroupOptimizationCommand : ICommand<OptimizationGroupSubmissionDto>, ITrialFilterOptions
{
    public required string StrategyName { get; init; }
    public required string OptimizationMethod { get; init; } // "BruteForce" or "Genetic"
    public Dictionary<string, OptimizationAxisOverride>? Axes { get; init; }
    public required List<List<DataFeedSubscription>> SubscriptionAxis { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = -1;
    public long MaxCombinations { get; init; } = 500_000;
    public int MaxTrialsToKeep { get; init; } = 10_000;
    public double? MinProfitFactor { get; init; }
    public double? MaxDrawdownPct { get; init; }
    public double? MinSharpeRatio { get; init; }
    public double? MinSortinoRatio { get; init; }
    public double? MinAnnualizedReturnPct { get; init; }
    public int? MinTradeCount { get; init; } = 30;
    public decimal? MinNetProfit { get; init; }
    public FitnessConfig? FitnessConfig { get; init; }
    public GeneticConfig? GeneticSettings { get; init; }
    public string? InputJson { get; init; }
    public bool Validate { get; init; }
    public string ThresholdProfileName { get; init; } = "Crypto-Standard";
    public int MaxThreads { get; init; }
}

public sealed record OptimizationGroupSubmissionDto
{
    public required Guid GroupId { get; init; }
    public required IReadOnlyList<GroupRunSubmissionDto> Runs { get; init; }
    public required long TotalCombinationsPerRun { get; init; }
}

public sealed record GroupRunSubmissionDto
{
    public required Guid Id { get; init; }
    public required IReadOnlyList<DataFeedSubscription> Dss { get; init; }
    public required long TotalCombinations { get; init; }
}
