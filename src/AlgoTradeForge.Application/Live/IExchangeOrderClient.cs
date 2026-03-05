namespace AlgoTradeForge.Application.Live;

public interface IExchangeOrderClient
{
    Task<long> PlaceOrderAsync(
        string symbol, string side, string type, decimal quantity,
        decimal? price = null, decimal? stopPrice = null,
        CancellationToken ct = default);

    Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default);
}
