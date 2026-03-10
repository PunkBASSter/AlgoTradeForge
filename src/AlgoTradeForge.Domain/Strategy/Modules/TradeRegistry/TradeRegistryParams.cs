using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

public sealed class TradeRegistryParams : ModuleParamsBase
{
    [Optimizable(Min = 0, Max = 20, Step = 1)]
    public int MaxConcurrentGroups { get; init; } = 0; // 0 = unlimited
}
