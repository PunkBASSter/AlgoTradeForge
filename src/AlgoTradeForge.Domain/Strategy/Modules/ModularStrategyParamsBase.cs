using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public class ModularStrategyParamsBase : StrategyParamsBase
{
    public virtual IMoneyManagementModule MoneyManagement { get; init; } = new FixedNotionalModule(new FixedNotionalParams());
}
