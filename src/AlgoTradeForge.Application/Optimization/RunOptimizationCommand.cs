using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Optimization;

public sealed record RunOptimizationCommand : ICommand<OptimizationResultDto>
{
    public required string StrategyName { get; init; }
    public Dictionary<string, OptimizationAxisOverride>? Axes { get; init; }
    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public long SlippageTicks { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = -1;
    public long MaxCombinations { get; init; } = 100_000;
    public string SortBy { get; init; } = "SharpeRatio";
}

public sealed record DataSubscriptionDto
{
    public required string Asset { get; init; }
    public required string Exchange { get; init; }
    public required string TimeFrame { get; init; }
}

public abstract record OptimizationAxisOverride;

public sealed record RangeOverride(decimal Min, decimal Max, decimal Step) : OptimizationAxisOverride;

public sealed record FixedOverride(object Value) : OptimizationAxisOverride;

public sealed record DiscreteSetOverride(IReadOnlyList<object> Values) : OptimizationAxisOverride;

public sealed record ModuleChoiceOverride(
    Dictionary<string, Dictionary<string, OptimizationAxisOverride>?> Variants) : OptimizationAxisOverride;
