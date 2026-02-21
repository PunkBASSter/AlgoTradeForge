using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public interface IOrderContext
{
    long Cash { get; }
    long Submit(Order order);
    Order? Cancel(long orderId);
    IReadOnlyList<Order> GetPendingOrders();
    IReadOnlyList<Fill> GetFills();
}
