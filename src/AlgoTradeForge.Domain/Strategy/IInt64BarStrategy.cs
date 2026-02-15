using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

public interface IInt64BarStrategy : IStrategy
{
    void OnBar(DateTimeOffset timestamp, Int64Bar bar, DataSubscription subscription, IOrderContext orders);
    
}
