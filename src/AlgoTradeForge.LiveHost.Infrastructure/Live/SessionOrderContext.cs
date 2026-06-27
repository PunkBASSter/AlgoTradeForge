using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Per-session facade over a shared, account-scoped LiveOrderContext. Tags every Submit
// with its session id so the originating strategy gets OnTrade; all reads/state delegate
// to the shared account ledger.
public sealed class SessionOrderContext(Guid sessionId, LiveOrderContext account) : IOrderContext
{
    public long Cash => account.Cash;
    public long UsedMargin => account.UsedMargin;
    public long AvailableMargin => account.AvailableMargin;
    public long Submit(Order order) => account.Submit(order, sessionId);
    public Order? Cancel(long orderId) => account.Cancel(orderId);
    public IReadOnlyList<Order> GetPendingOrders() => account.GetPendingOrders();
    public IReadOnlyList<Fill> GetFills() => account.GetFills();
    public IReadOnlyDictionary<string, Position> GetPositions() => account.GetPositions();
}
