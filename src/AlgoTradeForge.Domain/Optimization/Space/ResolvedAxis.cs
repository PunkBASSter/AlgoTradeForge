namespace AlgoTradeForge.Domain.Optimization.Space;

public abstract record ResolvedAxis(string Name);

public sealed record ResolvedNumericAxis(
    string Name,
    IReadOnlyList<object> Values) : ResolvedAxis(Name);

public sealed record ResolvedDiscreteAxis(
    string Name,
    IReadOnlyList<object> Values) : ResolvedAxis(Name);

public sealed record ResolvedModuleSlotAxis(
    string Name,
    IReadOnlyList<ResolvedModuleVariant> Variants) : ResolvedAxis(Name);

public sealed record ResolvedModuleVariant(
    string TypeKey,
    IReadOnlyList<ResolvedAxis> SubAxes);
