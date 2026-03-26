namespace AlgoTradeForge.WebApi.Contracts;

public sealed record StrategyDescriptorResponse
{
    public required string Name { get; init; }
    public required Dictionary<string, object> ParameterDefaults { get; init; }
    public required List<ParameterAxisResponse> OptimizationAxes { get; init; }
    public required Dictionary<string, object> BacktestTemplate { get; init; }
    public required Dictionary<string, object> OptimizationTemplate { get; init; }
    public required Dictionary<string, object> LiveSessionTemplate { get; init; }
    public required Dictionary<string, object> DebugSessionTemplate { get; init; }
    public required Dictionary<string, object> GeneticOptimizationTemplate { get; init; }
}

public sealed record ParameterAxisResponse
{
    public required string Name { get; init; }
    public required string Type { get; init; }   // "numeric" | "discrete" | "module"
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public decimal? Step { get; init; }
    public string? ClrType { get; init; }
    public string? Unit { get; init; }
    public List<string>? DiscreteValues { get; init; }
    public List<ModuleVariantResponse>? Variants { get; init; }
}

public sealed record ModuleVariantResponse
{
    public required string TypeKey { get; init; }
    public required List<ParameterAxisResponse> Axes { get; init; }
}
