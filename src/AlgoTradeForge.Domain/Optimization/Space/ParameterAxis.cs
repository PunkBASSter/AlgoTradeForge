namespace AlgoTradeForge.Domain.Optimization.Space;

public abstract record ParameterAxis(string Name);

public sealed record NumericRangeAxis(
    string Name,
    decimal Min,
    decimal Max,
    decimal Step,
    Type ClrType) : ParameterAxis(Name);

public sealed record DiscreteSetAxis(
    string Name,
    IReadOnlyList<object> Values,
    Type ClrType) : ParameterAxis(Name);

public sealed record ModuleSlotAxis(
    string Name,
    Type ModuleInterface,
    IReadOnlyList<ModuleVariantDescriptor> Variants) : ParameterAxis(Name);
