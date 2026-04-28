using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

public interface IInt64BarStrategy : IStrategy
{
    void OnBarStart(Int64Bar bar, DataSubscription subscription) { }
    void OnBarComplete(Int64Bar bar, DataSubscription subscription);
}
