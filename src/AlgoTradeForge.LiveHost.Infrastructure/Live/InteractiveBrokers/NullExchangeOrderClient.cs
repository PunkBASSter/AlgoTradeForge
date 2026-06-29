using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Connector-level placeholder for the IB reconciler seam. IB order clients are per-account (per-target),
// so a single connector-level open-order query has no meaning here. GetOpenOrdersAsync returns empty,
// which is ONLY safe while the reconcile loop is not running — an empty result makes DetectAsync treat
// every expected protective order as missing and re-submit duplicates. E1 replaces this with the real
// per-target union client and starts the reconcile loop at that point.
internal sealed class NullExchangeOrderClient : IExchangeOrderClient
{
    public static readonly NullExchangeOrderClient Instance = new();

    private NullExchangeOrderClient() { }

    public Task<ExchangeOrderResult> PlaceOrderAsync(
        string symbol, OrderSide side, OrderType type, decimal quantity,
        decimal? price = null, decimal? stopPrice = null, CancellationToken ct = default) =>
        throw new NotSupportedException("NullExchangeOrderClient is reconcile-only; orders route per-account via IbExchangeOrderClient.");

    public Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ExchangeOpenOrder>> GetOpenOrdersAsync(string symbol, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeOpenOrder>>([]);

    public Task CancelAllOpenOrdersAsync(string symbol, CancellationToken ct = default) =>
        Task.CompletedTask;
}
