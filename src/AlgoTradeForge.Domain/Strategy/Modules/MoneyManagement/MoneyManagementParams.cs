using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public sealed class MoneyManagementParams : ModuleParamsBase
{
    [Optimizable(Include = ["FixedFractional", "AtrVolTarget", "HalfKelly"])]
    public SizingMethod Method { get; init; } = SizingMethod.FixedFractional;

    [Optimizable(Min = 0.5, Max = 5.0, Step = 0.5)]
    public double RiskPercent { get; init; } = 1.0;

    [Optimizable(Min = 0.05, Max = 0.3, Step = 0.05)]
    public double VolTarget { get; init; } = 0.15;

    [Optimizable(Min = 0.3, Max = 0.7, Step = 0.05)]
    public double WinRate { get; init; } = 0.5;

    [Optimizable(Min = 1.0, Max = 4.0, Step = 0.5)]
    public double PayoffRatio { get; init; } = 2.0;
}
