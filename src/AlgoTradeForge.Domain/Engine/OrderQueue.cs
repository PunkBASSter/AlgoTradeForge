using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed class OrderQueue
{
    private readonly List<Order> _orders = [];
    private readonly List<Order> _pendingBuffer = [];

    public void Submit(Order order) => _orders.Add(order);

    public bool Cancel(long orderId)
    {
        var order = _orders.Find(o => o.Id == orderId);
        if (order is null || order.Status is OrderStatus.Filled or OrderStatus.Cancelled)
            return false;

        order.Status = OrderStatus.Cancelled;
        _orders.Remove(order);
        return true;
    }

    public IReadOnlyList<Order> GetPendingForAsset(Asset asset)
    {
        _pendingBuffer.Clear();
        foreach (var o in _orders)
        {
            if (o.Asset == asset && o.Status is OrderStatus.Pending or OrderStatus.Triggered)
                _pendingBuffer.Add(o);
        }
        return _pendingBuffer;
    }

    public IReadOnlyList<Order> GetAll() => _orders;

    public void Remove(long orderId) => _orders.RemoveAll(o => o.Id == orderId);

    public int Count => _orders.Count;
}
