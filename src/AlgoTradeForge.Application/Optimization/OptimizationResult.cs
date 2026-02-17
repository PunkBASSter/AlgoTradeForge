using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Optimization;

public sealed record OptimizationResultDto
{
    public required string StrategyName { get; init; }
    public required long TotalCombinations { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required IReadOnlyList<OptimizationTrialResultDto> Trials { get; init; }
}

public sealed record OptimizationTrialResultDto
{
    public required IReadOnlyDictionary<string, object> Parameters { get; init; }
    public required PerformanceMetrics Metrics { get; init; }
    public required TimeSpan Duration { get; init; }
}
