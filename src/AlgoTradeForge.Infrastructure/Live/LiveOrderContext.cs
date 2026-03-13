using System.Collections.Concurrent;
using System.Threading.Channels;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Collections;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live;

public sealed class LiveOrderContext : IOrderContext
{
    private readonly IExchangeOrderClient _orderClient;
    private readonly IOrderValidator _orderValidator;
    private readonly Portfolio _portfolio;
    private readonly Asset _primaryAsset;
    private readonly ILogger _logger;
    private readonly Guid _sessionId;
    private readonly ConcurrentDictionary<long, Guid> _exchangeOrderToSession;

    private readonly ConcurrentDictionary<long, Order> _pendingOrders = new();
    private readonly ConcurrentDictionary<long, long> _localToExchangeId = new();
    private readonly ConcurrentDictionary<long, byte> _restFilledOrders = new();
    private readonly Lock _recentFillsLock = new();
    private readonly List<Fill> _recentFills = [];
    private readonly RingBuffer<Fill> _allFills = new(1_000);
    private long _nextOrderId;

    private readonly Channel<OrderRequest> _orderChannel = Channel.CreateUnbounded<OrderRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<CancelRequest> _cancelChannel = Channel.CreateUnbounded<CancelRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    private Task? _orderProcessingTask;
    private Task? _cancelProcessingTask;
    private CancellationTokenSource? _cts;

    private sealed record OrderRequest(Order Order, long LocalId);
    private sealed record CancelRequest(Order Order, long ExchangeOrderId);

    public LiveOrderContext(
        Portfolio portfolio,
        Asset primaryAsset,
        IOrderValidator orderValidator,
        ILogger logger,
        IExchangeOrderClient orderClient,
        Guid sessionId,
        ConcurrentDictionary<long, Guid> exchangeOrderToSession)
    {
        _portfolio = portfolio;
        _primaryAsset = primaryAsset;
        _orderValidator = orderValidator;
        _logger = logger;
        _orderClient = orderClient;
        _sessionId = sessionId;
        _exchangeOrderToSession = exchangeOrderToSession;
    }

    public long Cash => _portfolio.Cash;
    public long UsedMargin => _portfolio.ComputeUsedMargin();
    public long AvailableMargin => Cash - UsedMargin;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _orderProcessingTask = Task.Run(() => ProcessOrdersAsync(token), token);
        _cancelProcessingTask = Task.Run(() => ProcessCancelsAsync(token), token);
    }

    public async Task StopAsync()
    {
        // Complete channels first so readers drain remaining items
        _orderChannel.Writer.TryComplete();
        _cancelChannel.Writer.TryComplete();

        // Await processing tasks before cancelling CTS so queued
        // orders/cancels are sent to the exchange
        if (_orderProcessingTask is not null)
            await IgnoreCancellation(_orderProcessingTask);
        if (_cancelProcessingTask is not null)
            await IgnoreCancellation(_cancelProcessingTask);

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }

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
        order.Status = OrderStatus.Pending;

        _pendingOrders.TryAdd(id, order);
        _orderChannel.Writer.TryWrite(new OrderRequest(order, id));

        return id;
    }

    public Order? Cancel(long orderId)
    {
        // Resolve to exchange order ID if mapped, otherwise use orderId as-is
        var exchangeOrderId = _localToExchangeId.TryGetValue(orderId, out var mapped) ? mapped : orderId;

        // Try remove by exchange ID first (post-placement), then by local ID (pre-placement)
        if (!_pendingOrders.TryRemove(exchangeOrderId, out var order) &&
            !_pendingOrders.TryRemove(orderId, out order))
            return null;

        order.Status = OrderStatus.Cancelled;
        _cancelChannel.Writer.TryWrite(new CancelRequest(order, exchangeOrderId));

        return order;
    }

    public IReadOnlyList<Order> GetPendingOrders() =>
        _pendingOrders.Values.ToList();

    public IReadOnlyList<Fill> GetFills()
    {
        lock (_recentFillsLock)
            return _recentFills.ToList();
    }

    public IReadOnlyDictionary<string, Position> GetPositions() =>
        _portfolio.Positions;

    internal void AddFill(Fill fill)
    {
        lock (_recentFillsLock)
        {
            _recentFills.Add(fill);
            _allFills.Add(fill);
            _portfolio.Apply(fill);
        }
    }

    internal IReadOnlyList<Fill> GetAllFills()
    {
        lock (_recentFillsLock)
            return _allFills.ToList();
    }

    internal void ClearRecentFills()
    {
        lock (_recentFillsLock)
            _recentFills.Clear();
    }

    internal Order? GetPendingOrder(long orderId) =>
        _pendingOrders.GetValueOrDefault(orderId);

    internal void RemovePendingOrder(long orderId) =>
        _pendingOrders.TryRemove(orderId, out _);

    internal void RekeyToExchangeId(long localId, long exchangeOrderId)
    {
        if (_pendingOrders.TryRemove(localId, out var order))
        {
            order.Id = exchangeOrderId;
            _pendingOrders.TryAdd(exchangeOrderId, order);
            _localToExchangeId.TryAdd(localId, exchangeOrderId);
            _exchangeOrderToSession.TryAdd(exchangeOrderId, _sessionId);
            OrderMapped?.Invoke(exchangeOrderId);
        }
    }

    internal event Action<long>? OrderMapped;

    internal long ResolveExchangeOrderId(long localOrderId) =>
        _localToExchangeId.TryGetValue(localOrderId, out var exchangeId) ? exchangeId : localOrderId;

    internal bool IsOrderRestFilled(long orderId) => _restFilledOrders.ContainsKey(orderId);

    internal Portfolio Portfolio => _portfolio;

    private async Task ProcessOrdersAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _orderChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var order = request.Order;
                    var scale = new ScaleContext(_primaryAsset);
                    decimal? price = order.LimitPrice.HasValue
                        ? scale.ToMarketPrice(order.LimitPrice.Value)
                        : null;
                    decimal? stopPrice = order.StopPrice.HasValue
                        ? scale.ToMarketPrice(order.StopPrice.Value)
                        : null;

                    var result = await _orderClient.PlaceOrderAsync(
                        order.Asset.Name, order.Side, order.Type, order.Quantity,
                        price, stopPrice, ct);

                    var exchangeOrderId = result.OrderId;

                    // Re-key from local ID to exchange order ID
                    _pendingOrders.TryRemove(request.LocalId, out var pending);

                    if (pending is not null)
                    {
                        pending.Id = exchangeOrderId;
                        _exchangeOrderToSession.TryAdd(exchangeOrderId, _sessionId);
                        _pendingOrders.TryAdd(exchangeOrderId, pending);
                        _localToExchangeId.TryAdd(request.LocalId, exchangeOrderId);
                        OrderMapped?.Invoke(exchangeOrderId);
                    }

                    // Process fills from REST response (reliable path for MARKET orders)
                    if (result.Fills.Count > 0)
                    {
                        _restFilledOrders.TryAdd(exchangeOrderId, 0);

                        foreach (var restFill in result.Fills)
                        {
                            var fill = new Fill(
                                exchangeOrderId,
                                order.Asset,
                                DateTimeOffset.UtcNow,
                                scale.FromMarketPrice(restFill.Price),
                                restFill.Quantity,
                                order.Side,
                                scale.FromMarketPrice(restFill.Commission));

                            AddFill(fill);
                        }

                        if (pending is not null)
                        {
                            pending.Status = OrderStatus.Filled;
                            RemovePendingOrder(exchangeOrderId);
                        }

                        _logger.LogInformation(
                            "Processed {FillCount} fill(s) from REST response for order {OrderId}",
                            result.Fills.Count, exchangeOrderId);
                    }

                    _logger.LogInformation(
                        "Order placed: {ExchangeOrderId} for local {LocalId} ({Side} {Qty} {Asset})",
                        exchangeOrderId, request.LocalId, order.Side, order.Quantity, order.Asset.Name);
                }
                catch (Exception ex)
                {
                    if (_pendingOrders.TryRemove(request.LocalId, out var rejected))
                        rejected.Status = OrderStatus.Rejected;

                    _logger.LogError(ex, "Failed to place order (local {LocalId})", request.LocalId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessCancelsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _cancelChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _orderClient.CancelOrderAsync(request.Order.Asset.Name, request.ExchangeOrderId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to cancel order {OrderId}", request.ExchangeOrderId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
