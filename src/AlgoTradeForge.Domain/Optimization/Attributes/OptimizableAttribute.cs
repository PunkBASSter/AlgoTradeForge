namespace AlgoTradeForge.Domain.Optimization.Attributes;

/// <summary>
/// Marks a numeric parameter as optimizable. Min/Max/Step use <c>double</c> because C# attribute
/// arguments must be compile-time constants; the builder converts them to <c>decimal</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizableAttribute : Attribute
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
}
