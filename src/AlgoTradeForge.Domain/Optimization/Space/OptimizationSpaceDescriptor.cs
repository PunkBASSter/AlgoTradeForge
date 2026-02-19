namespace AlgoTradeForge.Domain.Optimization.Space;

public interface IOptimizationSpaceDescriptor
{
    string StrategyName { get; }
    Type StrategyType { get; }
    Type ParamsType { get; }
    IReadOnlyList<ParameterAxis> Axes { get; }
}

public sealed class OptimizationSpaceDescriptor(
    string strategyName,
    Type strategyType,
    Type paramsType,
    IReadOnlyList<ParameterAxis> axes) : IOptimizationSpaceDescriptor
{
    public string StrategyName { get; } = strategyName;
    public Type StrategyType { get; } = strategyType;
    public Type ParamsType { get; } = paramsType;
    public IReadOnlyList<ParameterAxis> Axes { get; } = axes;
}
