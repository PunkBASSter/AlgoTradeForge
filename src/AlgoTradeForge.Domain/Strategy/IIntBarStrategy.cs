using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public interface IIntBarStrategy
{
    IList<DataSubscription> DataSubscriptions { get; }

    void OnBar(Int64Bar bar, DataSubscription subscription, IOrderContext orders);

    /// <summary>
    /// Called by the engine for each fill event.
    /// </summary>
    void OnTrade(Fill fill, Order order);
}
