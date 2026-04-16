using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public class ModularStrategyParamsBase : StrategyParamsBase
{
    [OptimizableModule]
    public IMoneyManagementModule MoneyManagement { get; init; } = new FixedFractionalModule(new FixedFractionalParams());
}
