namespace AlgoTradeForge.Application.Live;

public interface IExchangeOrderClient
{
    Task<ExchangeOrderResult> PlaceOrderAsync(
        string symbol, string side, string type, decimal quantity,
        decimal? price = null, decimal? stopPrice = null,
        CancellationToken ct = default);

    Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default);
}

public sealed record ExchangeOrderResult(long OrderId, IReadOnlyList<ExchangeFill> Fills);

public sealed record ExchangeFill(decimal Price, decimal Quantity, decimal Commission);
