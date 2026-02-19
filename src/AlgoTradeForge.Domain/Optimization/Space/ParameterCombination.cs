namespace AlgoTradeForge.Domain.Optimization.Space;

public sealed record ModuleSelection(string TypeKey, IReadOnlyDictionary<string, object> Params);

public sealed class ParameterCombination(IReadOnlyDictionary<string, object> values)
{
    public IReadOnlyDictionary<string, object> Values { get; } = values;
}
