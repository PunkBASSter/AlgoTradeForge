using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public sealed class HalfKellyParams : ModuleParamsBase
{
    [Optimizable(Min = 0.3, Max = 0.7, Step = 0.05)]
    public double WinRate { get; init; } = 0.5;

    [Optimizable(Min = 1.0, Max = 4.0, Step = 0.5)]
    public double PayoffRatio { get; init; } = 2.0;
}
