using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

internal sealed class BacktestOrderContext : IOrderContext
{
    private readonly OrderQueue _queue;
    private readonly List<Fill> _allFills;
    private readonly Portfolio _portfolio;
    private int _fillSnapshotStart;

    public BacktestOrderContext(OrderQueue queue, List<Fill> allFills, Portfolio portfolio)
    {
        _queue = queue;
        _allFills = allFills;
        _portfolio = portfolio;
    }

    public long Cash => _portfolio.Cash;

    public void BeginBar(int currentFillCount)
    {
        _fillSnapshotStart = currentFillCount;
    }

    public long Submit(Order order)
    {
        _queue.Submit(order);
        return order.Id;
    }

    public bool Cancel(long orderId) => _queue.Cancel(orderId);

    public IReadOnlyList<Order> GetPendingOrders() => _queue.GetAll();

    public IReadOnlyList<Fill> GetFills()
    {
        if (_fillSnapshotStart >= _allFills.Count)
            return [];
        return _allFills.GetRange(_fillSnapshotStart, _allFills.Count - _fillSnapshotStart);
    }
}
