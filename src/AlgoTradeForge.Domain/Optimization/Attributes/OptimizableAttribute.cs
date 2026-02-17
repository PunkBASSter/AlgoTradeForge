namespace AlgoTradeForge.Domain.Optimization.Attributes;

[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizableAttribute : Attribute
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
}
