using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Per-account adapter: maps domain PlaceOrderAsync/CancelOrderAsync onto the shared IIbOrderGateway.
// An account is single-asset for Plan 4 scope; the factory (C3) passes the execution asset at construction.
internal sealed class IbExchangeOrderClient(
    string account,
    Asset executionAsset,
    IIbOrderGateway gateway,
    IIbContractResolver contractResolver) : IExchangeOrderClient
{
    public async Task<ExchangeOrderResult> PlaceOrderAsync(
        string symbol, OrderSide side, OrderType type, decimal quantity,
        decimal? price = null, decimal? stopPrice = null,
        CancellationToken ct = default)
    {
        if (symbol != executionAsset.Name)
            throw new NotSupportedException(
                $"IbExchangeOrderClient for account '{account}' is bound to asset '{executionAsset.Name}'; " +
                $"cannot place order for '{symbol}'. Multi-asset-per-account is out of Plan 4 scope.");

        var resolved = await contractResolver.Resolve(executionAsset.ToIbContract(), ct);
        var request = BuildRequest(side, type, quantity, price, stopPrice);
        var orderId = await gateway.Place(account, executionAsset, resolved, request, side, type, quantity, ct);
        return new ExchangeOrderResult(orderId, []);
    }

    public Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default)
    {
        gateway.Cancel(orderId);
        return Task.CompletedTask;
    }

    private IbOrderRequest BuildRequest(OrderSide side, OrderType type, decimal quantity, decimal? price, decimal? stopPrice)
    {
        var action = side == OrderSide.Buy ? "BUY" : "SELL";
        return type switch
        {
            OrderType.Market => new IbOrderRequest(account, action, "MKT", quantity, LmtPrice: null, AuxPrice: null),
            OrderType.Limit  => new IbOrderRequest(account, action, "LMT", quantity, LmtPrice: (double?)price, AuxPrice: null),
            OrderType.Stop   => new IbOrderRequest(account, action, "STP", quantity, LmtPrice: null, AuxPrice: (double?)stopPrice),
            _ => throw new NotSupportedException($"OrderType '{type}' is not mapped for IB in Plan 4 scope."),
        };
    }
}
