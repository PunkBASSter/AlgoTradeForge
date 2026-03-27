using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Optimization;

public sealed record RunGeneticOptimizationCommand : ICommand<OptimizationSubmissionDto>, ITrialFilterOptions
{
    public required string StrategyName { get; init; }
    public Dictionary<string, OptimizationAxisOverride>? Axes { get; init; }
    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public List<DataSubscriptionDto>? SubscriptionAxis { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = -1;
    public string SortBy { get; init; } = MetricNames.Default;
    public int MaxTrialsToKeep { get; init; } = 10_000;
    public double? MinProfitFactor { get; init; }
    public double? MaxDrawdownPct { get; init; }
    public double? MinSharpeRatio { get; init; }
    public double? MinSortinoRatio { get; init; }
    public double? MinAnnualizedReturnPct { get; init; }
    public required GeneticConfig GeneticSettings { get; init; }
    public string? InputJson { get; init; }
}
