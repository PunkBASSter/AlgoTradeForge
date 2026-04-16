using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public class ModularStrategyParamsBase : StrategyParamsBase
{
    [OptimizableModule]
    public IMoneyManagementModule MoneyManagement { get; init; } = new FixedFractionalModule(new FixedFractionalParams());
    public TradeRegistryParams TradeRegistry { get; init; } = new();
}
