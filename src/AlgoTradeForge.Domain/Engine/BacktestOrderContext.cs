using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

internal sealed class BacktestOrderContext : IOrderContext
{
    private readonly OrderQueue _queue;
    private readonly List<Fill> _allFills;
    private readonly Portfolio _portfolio;
    private readonly IEventBus _bus;
    private readonly bool _busActive;
    private int _fillSnapshotStart;
    private DateTimeOffset _currentTimestamp;

    public BacktestOrderContext(OrderQueue queue, List<Fill> allFills, Portfolio portfolio, IEventBus bus)
    {
        _queue = queue;
        _allFills = allFills;
        _portfolio = portfolio;
        _bus = bus;
        _busActive = bus is not NullEventBus;
    }

    public long Cash => _portfolio.Cash;

    public void BeginBar(int currentFillCount, DateTimeOffset timestamp)
    {
        _fillSnapshotStart = currentFillCount;
        _currentTimestamp = timestamp;
    }

    public long Submit(Order order)
    {
        _queue.Submit(order);
        return order.Id;
    }

    public Order? Cancel(long orderId)
    {
        var order = _queue.Cancel(orderId);
        if (order is not null && _busActive)
        {
            _bus.Emit(new OrderCancelEvent(
                _currentTimestamp,
                "engine",
                order.Id,
                order.Asset.Name,
                "Strategy cancelled"));
        }
        return order;
    }

    public IReadOnlyList<Order> GetPendingOrders() => _queue.GetAll();

    public IReadOnlyList<Fill> GetFills()
    {
        if (_fillSnapshotStart >= _allFills.Count)
            return [];
        return _allFills.GetRange(_fillSnapshotStart, _allFills.Count - _fillSnapshotStart);
    }
}
