using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Order-plane seam over the shared IB socket: C1's IbExchangeOrderClient places/cancels through this so it is
// unit-testable over a mock. The gateway captures per-order context (asset/side/type/qty) at Place and joins
// it to inbound fills to build the neutral ExecutionReport off the pump thread.
internal interface IIbOrderGateway
{
    // Allocates an order id, places, awaits the first broker ack (bounded timeout); throws IbRequestException
    // on a reject-coded ack fault or TimeoutException/OCE on timeout. Returns the IB order id.
    Task<long> Place(string account, Asset asset, ResolvedIbContract contract, IbOrderRequest request,
        OrderSide side, OrderType type, decimal originalQuantity, CancellationToken ct = default);

    void Cancel(long orderId);
}
