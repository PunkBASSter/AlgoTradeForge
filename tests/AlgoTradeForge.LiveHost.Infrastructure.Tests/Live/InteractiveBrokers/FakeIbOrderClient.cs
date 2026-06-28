using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Records placed/cancelled orders and seeds NextOrderId. Helpers fire the wrapper's order callbacks
// to simulate broker acks (orderStatus / error) so the gateway's awaiter resolves without a live socket.
internal sealed class FakeIbOrderClient(int seedId) : IIbOrderClient
{
    private int _nextId = seedId;

    public int LastPlacedOrderId { get; private set; } = -1;
    public ResolvedIbContract? LastPlacedContract { get; private set; }
    public IbOrderRequest? LastPlacedRequest { get; private set; }
    public List<int> Cancelled { get; } = [];

    public int NextOrderId() => _nextId++;

    public void PlaceOrder(int orderId, ResolvedIbContract contract, IbOrderRequest request)
    {
        LastPlacedOrderId = orderId;
        LastPlacedContract = contract;
        LastPlacedRequest = request;
    }

    public void CancelOrder(int orderId) => Cancelled.Add(orderId);

    public int OpenOrdersRequested { get; private set; }
    public void RequestOpenOrders() => OpenOrdersRequested++;

    // Drives the wrapper's orderStatus callback for the last placed id, completing the gateway's ack.
    public void SignalAck(IbWrapper wrapper, string status) =>
        wrapper.orderStatus(LastPlacedOrderId, status, 0, 1, 0, 0, 0, 0, 0, "", 0);
}
