namespace AlgoTradeForge.Application.Optimization;

public abstract record OptimizationAxisOverride;

public sealed record RangeOverride(decimal Min, decimal Max, decimal Step) : OptimizationAxisOverride;

public sealed record FixedOverride(object Value) : OptimizationAxisOverride;

public sealed record DiscreteSetOverride(IReadOnlyList<object> Values) : OptimizationAxisOverride;

public sealed record ModuleChoiceOverride(
    Dictionary<string, Dictionary<string, OptimizationAxisOverride>?> Variants) : OptimizationAxisOverride;
