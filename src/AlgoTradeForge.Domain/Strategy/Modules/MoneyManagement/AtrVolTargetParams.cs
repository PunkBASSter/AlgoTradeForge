using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public sealed class AtrVolTargetParams : ModuleParamsBase
{
    [Optimizable(Min = 0.05, Max = 0.3, Step = 0.05)]
    public double VolTarget { get; init; } = 0.15;
}
