namespace AlgoTradeForge.Domain.Optimization.Space;

public sealed record ModuleVariantDescriptor(
    string TypeKey,
    Type ImplType,
    Type ParamsType,
    IReadOnlyList<ParameterAxis> Axes);
