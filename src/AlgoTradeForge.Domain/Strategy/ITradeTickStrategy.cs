using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

public interface ITradeTickStrategy : IStrategy
{
    void OnTradeTick(in TradeTick tick, DataSubscription subscription);
}
