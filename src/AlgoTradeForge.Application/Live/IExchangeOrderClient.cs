using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Application.Live;

public interface IExchangeOrderClient
{
    Task<ExchangeOrderResult> PlaceOrderAsync(
        string symbol, OrderSide side, OrderType type, decimal quantity,
        decimal? price = null, decimal? stopPrice = null,
        CancellationToken ct = default);

    Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default);

    Task<IReadOnlyList<ExchangeOpenOrder>> GetOpenOrdersAsync(
        string symbol, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeOpenOrder>>([]);

    Task CancelAllOpenOrdersAsync(string symbol, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed record ExchangeOrderResult(long OrderId, IReadOnlyList<ExchangeFill> Fills);

public sealed record ExchangeFill(decimal Price, decimal Quantity, decimal Commission);

public sealed record ExchangeOpenOrder(
    long OrderId, string Symbol, string Side, string Type,
    decimal OriginalQuantity, decimal Price, decimal StopPrice, string Status);
