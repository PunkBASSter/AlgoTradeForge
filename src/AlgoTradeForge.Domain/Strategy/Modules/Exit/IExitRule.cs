using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

public interface IExitRule
{
    string Name { get; }
    int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group);
}
