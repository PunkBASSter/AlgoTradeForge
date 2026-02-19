namespace AlgoTradeForge.Domain.Optimization.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public sealed class StrategyKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
