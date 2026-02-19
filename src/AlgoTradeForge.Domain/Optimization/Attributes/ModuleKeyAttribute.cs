namespace AlgoTradeForge.Domain.Optimization.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
