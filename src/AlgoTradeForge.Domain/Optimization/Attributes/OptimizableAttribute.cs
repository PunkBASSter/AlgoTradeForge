namespace AlgoTradeForge.Domain.Optimization.Attributes;

/// <summary>
/// Marks a parameter as optimizable. For numeric types, Min/Max/Step define the range.
/// For enum types, all values are included by default; use <see cref="Include"/> or
/// <see cref="Exclude"/> to select a subset. Min/Max/Step are ignored for enums.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizableAttribute : Attribute
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
    public ParamUnit Unit { get; init; } = ParamUnit.Raw;

    /// <summary>
    /// For enum types: only include these values (by name or integer string).
    /// Mutually exclusive with <see cref="Exclude"/>.
    /// </summary>
    public string[]? Include { get; init; }

    /// <summary>
    /// For enum types: exclude these values (by name or integer string).
    /// Mutually exclusive with <see cref="Include"/>.
    /// </summary>
    public string[]? Exclude { get; init; }
}
