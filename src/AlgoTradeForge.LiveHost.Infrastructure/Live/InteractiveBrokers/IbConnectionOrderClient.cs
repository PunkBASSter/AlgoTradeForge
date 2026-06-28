namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbOrderClient: places/cancels on the shared socket. IBApi vocabulary stops here.
// BuildIbOrder is pure + unit-tested: Tif is ALWAYS "DAY" — an empty Tif draws IB error 10052 (order reject).
internal sealed class IbConnectionOrderClient(IbConnection connection) : IIbOrderClient
{
    public int NextOrderId() => connection.NextOrderId();

    public void PlaceOrder(int orderId, ResolvedIbContract contract, IbOrderRequest request) =>
        connection.Client.placeOrder(orderId, contract.ToIbApiContract(), BuildIbOrder(request));

    public void CancelOrder(int orderId) =>
        connection.Client.cancelOrder(orderId, new IBApi.OrderCancel());

    internal static IBApi.Order BuildIbOrder(IbOrderRequest request)
    {
        var order = new IBApi.Order
        {
            Action = request.Action,
            OrderType = request.OrderType,
            TotalQuantity = request.Quantity,
            Tif = "DAY", // mandatory: empty Tif => IB 10052 order reject
            Account = request.Account,
        };
        if (request.LmtPrice.HasValue)
            order.LmtPrice = request.LmtPrice.Value;
        if (request.AuxPrice.HasValue)
            order.AuxPrice = request.AuxPrice.Value;
        return order;
    }
}
