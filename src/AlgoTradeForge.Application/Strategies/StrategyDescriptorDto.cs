using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Strategies;

public sealed record StrategyDescriptorDto(
    string Name,
    IReadOnlyDictionary<string, object> ParameterDefaults,
    IReadOnlyList<ParameterAxis> Axes,
    IReadOnlyDictionary<string, object> BacktestTemplate,
    IReadOnlyDictionary<string, object> OptimizationTemplate,
    IReadOnlyDictionary<string, object> LiveSessionTemplate,
    IReadOnlyDictionary<string, object> DebugSessionTemplate);
