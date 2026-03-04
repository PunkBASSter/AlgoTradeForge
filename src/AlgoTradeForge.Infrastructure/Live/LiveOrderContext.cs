using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live;

public sealed class LiveOrderContext : IOrderContext
{
    private readonly BinanceApiClient _apiClient;
    private readonly IOrderValidator _orderValidator;
    private readonly Portfolio _portfolio;
    private readonly Asset _primaryAsset;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<long, Order> _pendingOrders = new();
    private readonly List<Fill> _recentFills = [];
    private long _nextOrderId;

    public LiveOrderContext(
        Portfolio portfolio,
        Asset primaryAsset,
        IOrderValidator orderValidator,
        ILogger logger,
        BinanceApiClient apiClient)
    {
        _portfolio = portfolio;
        _primaryAsset = primaryAsset;
        _orderValidator = orderValidator;
        _logger = logger;
        _apiClient = apiClient;
    }

    public long Cash => _portfolio.Cash;

    public long Submit(Order order)
    {
        var rejection = _orderValidator.ValidateSubmission(order);
        if (rejection is not null)
        {
            order.Status = OrderStatus.Rejected;
            _logger.LogWarning("Order rejected: {Reason}", rejection);
            return order.Id;
        }

        var id = Interlocked.Increment(ref _nextOrderId);
        order.Id = id;
        order.SubmittedAt = DateTimeOffset.UtcNow;

        try
        {
            var binanceType = MapOrderType(order.Type);
            var side = order.Side == OrderSide.Buy ? "BUY" : "SELL";
            decimal? price = order.LimitPrice.HasValue
                ? order.LimitPrice.Value * _primaryAsset.TickSize
                : null;
            decimal? stopPrice = order.StopPrice.HasValue
                ? order.StopPrice.Value * _primaryAsset.TickSize
                : null;

            var response = _apiClient.PlaceOrderAsync(
                order.Asset.Name, side, binanceType, order.Quantity,
                price, stopPrice).GetAwaiter().GetResult();

            if (order.Type == OrderType.Market)
                order.Status = OrderStatus.Filled;
            else
                _pendingOrders.TryAdd(id, order);

            _logger.LogInformation("Order placed: {BinanceOrderId} for local {LocalId} ({Side} {Qty} {Asset})",
                response.OrderId, id, order.Side, order.Quantity, order.Asset.Name);
        }
        catch (Exception ex)
        {
            order.Status = OrderStatus.Rejected;
            _logger.LogError(ex, "Failed to place order");
        }

        return id;
    }

    public Order? Cancel(long orderId)
    {
        if (!_pendingOrders.TryRemove(orderId, out var order))
            return null;

        order.Status = OrderStatus.Cancelled;

        try
        {
            _apiClient.CancelOrderAsync(order.Asset.Name, orderId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId}", orderId);
        }

        return order;
    }

    public IReadOnlyList<Order> GetPendingOrders() =>
        _pendingOrders.Values.ToList();

    public IReadOnlyList<Fill> GetFills()
    {
        lock (_recentFills)
            return _recentFills.ToList();
    }

    public IReadOnlyDictionary<string, Position> GetPositions() =>
        _portfolio.Positions;

    internal void AddFill(Fill fill)
    {
        lock (_recentFills)
            _recentFills.Add(fill);
        _portfolio.Apply(fill);
    }

    internal void ClearRecentFills()
    {
        lock (_recentFills)
            _recentFills.Clear();
    }

    internal Order? GetPendingOrder(long orderId) =>
        _pendingOrders.GetValueOrDefault(orderId);

    internal void RemovePendingOrder(long orderId) =>
        _pendingOrders.TryRemove(orderId, out _);

    internal Portfolio Portfolio => _portfolio;

    private static string MapOrderType(OrderType type) => type switch
    {
        OrderType.Market => "MARKET",
        OrderType.Limit => "LIMIT",
        OrderType.Stop => "STOP_LOSS",
        OrderType.StopLimit => "STOP_LOSS_LIMIT",
        _ => throw new ArgumentException($"Unsupported order type: {type}"),
    };
}
