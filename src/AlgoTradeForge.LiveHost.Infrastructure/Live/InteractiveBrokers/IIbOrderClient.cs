namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal interface IIbOrderClient
{
    int NextOrderId();
    void PlaceOrder(int orderId, ResolvedIbContract contract, IbOrderRequest request);
    void CancelOrder(int orderId);
}

internal readonly record struct IbOrderRequest(string Account, string Action, string OrderType, decimal Quantity, double? LmtPrice, double? AuxPrice);
