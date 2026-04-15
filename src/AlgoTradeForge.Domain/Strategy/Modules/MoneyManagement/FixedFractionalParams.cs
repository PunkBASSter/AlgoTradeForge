using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public sealed class FixedFractionalParams : ModuleParamsBase
{
    [Optimizable(Min = 0.5, Max = 5.0, Step = 0.5)]
    public double RiskPercent { get; init; } = 1.0;
}
