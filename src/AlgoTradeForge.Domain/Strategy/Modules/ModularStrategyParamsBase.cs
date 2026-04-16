using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public class ModularStrategyParamsBase : StrategyParamsBase
{
    [Optimizable(Min = 10, Max = 80, Step = 10)]
    public int SignalThreshold { get; init; } = 30;

    [Optimizable(Min = -100, Max = -20, Step = 10)]
    public int ExitThreshold { get; init; } = -50;

    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double DefaultAtrStopMultiplier { get; init; } = 2.0;

    [OptimizableModule]
    public IMoneyManagementModule MoneyManagement { get; init; } = new FixedFractionalModule(new FixedFractionalParams());
    public TradeRegistryParams TradeRegistry { get; init; } = new();
}
