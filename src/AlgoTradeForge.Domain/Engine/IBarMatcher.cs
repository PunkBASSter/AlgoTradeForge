using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public interface IBarMatcher
{
    Fill? TryFill(Order order, IntBar bar, BacktestOptions options);
}
