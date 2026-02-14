using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public interface IBarMatcher
{
    Fill? TryFill(Order order, Int64Bar bar, BacktestOptions options);
}
