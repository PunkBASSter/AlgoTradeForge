namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal interface IIbOrderClient
{
    int NextOrderId();
    void PlaceOrder(int orderId, ResolvedIbContract contract, IbOrderRequest request);
    void CancelOrder(int orderId);

    // One-shot pull of every open order on the socket (reqAllOpenOrders) → openOrder*/openOrderEnd pushback.
    // The reconnect reconciliation arms IbWrapper.BeginOpenOrderSnapshot() before calling this.
    void RequestOpenOrders();
}

internal readonly record struct IbOrderRequest(string Account, string Action, string OrderType, decimal Quantity, double? LmtPrice, double? AuxPrice);
