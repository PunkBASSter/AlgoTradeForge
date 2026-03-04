using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly Guid _sessionId;
    private readonly ConcurrentDictionary<long, Guid> _binanceOrderToSession;

    private readonly ConcurrentDictionary<long, Order> _pendingOrders = new();
    private readonly ConcurrentDictionary<long, long> _localToBinanceId = new();
    private readonly Lock _recentFillsLock = new();
    private readonly List<Fill> _recentFills = [];
    private long _nextOrderId;

    private readonly Channel<OrderRequest> _orderChannel = Channel.CreateUnbounded<OrderRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<CancelRequest> _cancelChannel = Channel.CreateUnbounded<CancelRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    private Task? _orderProcessingTask;
    private Task? _cancelProcessingTask;
    private CancellationTokenSource? _cts;

    private sealed record OrderRequest(Order Order, long LocalId);
    private sealed record CancelRequest(Order Order, long BinanceOrderId);

    public LiveOrderContext(
        Portfolio portfolio,
        Asset primaryAsset,
        IOrderValidator orderValidator,
        ILogger logger,
        BinanceApiClient apiClient,
        Guid sessionId,
        ConcurrentDictionary<long, Guid> binanceOrderToSession)
    {
        _portfolio = portfolio;
        _primaryAsset = primaryAsset;
        _orderValidator = orderValidator;
        _logger = logger;
        _apiClient = apiClient;
        _sessionId = sessionId;
        _binanceOrderToSession = binanceOrderToSession;
    }

    public long Cash => _portfolio.Cash;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _orderProcessingTask = Task.Run(() => ProcessOrdersAsync(token), token);
        _cancelProcessingTask = Task.Run(() => ProcessCancelsAsync(token), token);
    }

    public async Task StopAsync()
    {
        _orderChannel.Writer.TryComplete();
        _cancelChannel.Writer.TryComplete();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        if (_orderProcessingTask is not null)
            await IgnoreCancellation(_orderProcessingTask);
        if (_cancelProcessingTask is not null)
            await IgnoreCancellation(_cancelProcessingTask);
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
        // Resolve to Binance order ID if mapped, otherwise use orderId as-is
        var binanceOrderId = _localToBinanceId.TryGetValue(orderId, out var mapped) ? mapped : orderId;

        // Try remove by Binance ID first (post-placement), then by local ID (pre-placement)
        if (!_pendingOrders.TryRemove(binanceOrderId, out var order) &&
            !_pendingOrders.TryRemove(orderId, out order))
            return null;

        order.Status = OrderStatus.Cancelled;
        _cancelChannel.Writer.TryWrite(new CancelRequest(order, binanceOrderId));

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
            _portfolio.Apply(fill);
        }
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

    internal void SimulateOrderPlaced(long localId, long binanceOrderId)
    {
        if (_pendingOrders.TryRemove(localId, out var order))
        {
            order.Id = binanceOrderId;
            _pendingOrders.TryAdd(binanceOrderId, order);
            _localToBinanceId.TryAdd(localId, binanceOrderId);
            _binanceOrderToSession.TryAdd(binanceOrderId, _sessionId);
        }
    }

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
                    var binanceType = MapOrderType(order.Type);
                    var side = order.Side == OrderSide.Buy ? "BUY" : "SELL";
                    decimal? price = order.LimitPrice.HasValue
                        ? order.LimitPrice.Value * _primaryAsset.TickSize
                        : null;
                    decimal? stopPrice = order.StopPrice.HasValue
                        ? order.StopPrice.Value * _primaryAsset.TickSize
                        : null;

                    var response = await _apiClient.PlaceOrderAsync(
                        order.Asset.Name, side, binanceType, order.Quantity,
                        price, stopPrice, ct);

                    // Re-key from local ID to Binance order ID
                    _pendingOrders.TryRemove(request.LocalId, out var pending);

                    if (pending is not null)
                    {
                        pending.Id = response.OrderId;
                        _binanceOrderToSession.TryAdd(response.OrderId, _sessionId);

                        if (order.Type == OrderType.Market)
                        {
                            pending.Status = OrderStatus.Filled;
                        }
                        else
                        {
                            _pendingOrders.TryAdd(response.OrderId, pending);
                            _localToBinanceId.TryAdd(request.LocalId, response.OrderId);
                        }
                    }

                    _logger.LogInformation(
                        "Order placed: {BinanceOrderId} for local {LocalId} ({Side} {Qty} {Asset})",
                        response.OrderId, request.LocalId, order.Side, order.Quantity, order.Asset.Name);
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
                    await _apiClient.CancelOrderAsync(request.Order.Asset.Name, request.BinanceOrderId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to cancel order {OrderId}", request.BinanceOrderId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string MapOrderType(OrderType type) => type switch
    {
        OrderType.Market => "MARKET",
        OrderType.Limit => "LIMIT",
        OrderType.Stop => "STOP_LOSS",
        OrderType.StopLimit => "STOP_LOSS_LIMIT",
        _ => throw new ArgumentException($"Unsupported order type: {type}"),
    };

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
