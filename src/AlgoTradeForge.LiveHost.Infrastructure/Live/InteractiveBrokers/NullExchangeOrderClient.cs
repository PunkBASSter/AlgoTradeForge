using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Connector-level no-op exchange client for the IB reconciler seam. IB order clients are per-account
// (per-target), so a single connector-level open-order query has no meaning yet — per-target union
// reconciliation is E1. Until then the reconcile loop sees an empty open-order set (nothing to repair/cancel).
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
