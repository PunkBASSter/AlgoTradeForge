using System.Collections.Concurrent;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The order plane over the shared IB socket. Place allocates an id, captures per-order context, places, and
// awaits the first broker ack. Fills (and terminal cancels) arrive on the EReader pump thread; the sink only
// TryWrites them onto a bounded lane (never blocks the pump). A single worker drains the lane, joins each event
// to its captured order context, and emits the neutral ExecutionReport off-pump.
internal sealed class IbOrderGateway : IIbOrderGateway, IAsyncDisposable
{
    private readonly record struct OrderContext(string Symbol, OrderSide Side, OrderType Type, decimal OriginalQuantity);

    // One ordered lane carries fills AND terminal cancels, so a cancel can never overtake a fill queued ahead
    // of it (which would re-emit the cancelled remainder as a phantom fill against an already-untracked order).
    private readonly record struct OrderLaneEvent(bool IsCancel, IbFill Fill, int CancelOrderId);

    private readonly IIbOrderClient _client;
    private readonly IbWrapper _wrapper;
    private readonly Action<ExecutionReport> _onReport;
    private readonly ILogger _logger;
    private readonly TimeSpan _ackTimeout;

    private readonly ConcurrentDictionary<int, OrderContext> _orderInfo = new();
    private readonly ConcurrentDictionary<int, decimal> _cumulativeFilled = new();
    private readonly Channel<OrderLaneEvent> _lane;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private long _droppedEvents;

    // Order-events lost to lane saturation. In a drop FullMode, Writer.TryWrite ALWAYS returns true, so loss
    // is observable ONLY via the itemDropped callback below — never the TryWrite return value.
    internal long DroppedFills => Interlocked.Read(ref _droppedEvents);

    // Live per-order entries (for leak assertions): pruned on every terminal (full fill / cancel).
    internal int TrackedOrderCount => _orderInfo.Count;

    public IbOrderGateway(IIbOrderClient client, IbWrapper wrapper, Action<ExecutionReport> onReport,
        ILogger logger, int laneCapacity = 4096, TimeSpan? ackTimeout = null)
    {
        _client = client;
        _wrapper = wrapper;
        _onReport = onReport;
        _logger = logger;
        _ackTimeout = ackTimeout ?? TimeSpan.FromSeconds(10);
        _lane = Channel.CreateBounded<OrderLaneEvent>(
            new BoundedChannelOptions(laneCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            itemDropped: dropped =>
            {
                var n = Interlocked.Increment(ref _droppedEvents);
                _logger.LogCritical(
                    "IB order-event lane full (order {OrderId}); event dropped — execution report lost (total {Count}).",
                    dropped.IsCancel ? dropped.CancelOrderId : dropped.Fill.OrderId, n);
            });

        _wrapper.RegisterOrderSink(onStatus: OnStatusFromPump, onFill: OnFillFromPump);
        _worker = Task.Run(DrainLane);
    }

    public async Task<long> Place(string account, Asset asset, ResolvedIbContract contract, IbOrderRequest request,
        OrderSide side, OrderType type, decimal originalQuantity, CancellationToken ct = default)
    {
        var id = _client.NextOrderId();
        _orderInfo[id] = new OrderContext(asset.Name, side, type, originalQuantity);
        var ack = _wrapper.RegisterOrderAck(id);
        try
        {
            _client.PlaceOrder(id, contract, request);
            await ack.WaitAsync(_ackTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _wrapper.ReleaseOrderAck(id);
        }
        return id;
    }

    public void Cancel(long orderId) => _client.CancelOrder((int)orderId);

    public async Task CancelAllOpenOrders(string account, CancellationToken ct = default)
    {
        var byAccount = await SnapshotOpenOrders(ct).ConfigureAwait(false);
        if (!byAccount.TryGetValue(account, out var ids))
            return;
        foreach (var id in ids)
            Cancel(id);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> SnapshotOpenOrders(CancellationToken ct = default)
    {
        // Arm the accumulator BEFORE requesting so the pushback (openOrder*/openOrderEnd on the pump) lands in it.
        var snapshot = _wrapper.BeginOpenOrderSnapshot();
        _client.RequestOpenOrders();
        var rows = await snapshot.WaitAsync(_ackTimeout, ct).ConfigureAwait(false);

        var byAccount = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            (byAccount.TryGetValue(row.Account, out var list) ? list : byAccount[row.Account] = []).Add(row.OrderId);

        return byAccount.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<long>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // Never blocks the pump: TryWrite always succeeds (DropWrite); saturation is reported via itemDropped.
    private void OnFillFromPump(IbFill fill) => _lane.Writer.TryWrite(new OrderLaneEvent(false, fill, 0));

    // Cancel confirmations terminate an order with no fill. Route them through the lane (ordered after any
    // queued fills) so the dispatcher untracks the order only after every fill for it has been delivered.
    private void OnStatusFromPump(IbOrderStatusUpdate s)
    {
        if (s.Status is "Cancelled" or "ApiCancelled")
            _lane.Writer.TryWrite(new OrderLaneEvent(true, default, s.OrderId));
    }

    private async Task DrainLane()
    {
        try
        {
            await foreach (var ev in _lane.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try { if (ev.IsCancel) EmitCancel(ev.CancelOrderId); else EmitFill(ev.Fill); }
                catch (Exception ex) when (!LiveSessionDispatcher.IsTrueShutdown(ex, _cts.Token))
                {
                    _logger.LogError(ex, "Failed to emit execution report for order {OrderId}.",
                        ev.IsCancel ? ev.CancelOrderId : ev.Fill.OrderId);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown */ }
    }

    private void EmitFill(IbFill fill)
    {
        var known = _orderInfo.TryGetValue(fill.OrderId, out var ctx);
        // Stored side is authoritative for orders this gateway placed. For unmapped fills (reconnect replay /
        // external orders), ctx is default; parse the IB side string and use the execution's own contract symbol.
        var side = known ? ctx.Side : ParseIbSide(fill.Side);

        // Terminal status is driven by cumulative fills vs the order's original quantity — robust to fill/status
        // stream ordering (the final fill itself carries Filled; no dependence on a possibly-later orderStatus).
        var cumulative = _cumulativeFilled.AddOrUpdate(fill.OrderId, fill.Qty, (_, prev) => prev + fill.Qty);
        var filled = !known || cumulative >= ctx.OriginalQuantity;

        _onReport(new ExecutionReport(
            OrderId: fill.OrderId,
            Symbol: known ? ctx.Symbol : fill.Symbol,
            Side: side,
            ExecType: ExecType.Trade,
            LastFillPrice: (decimal)fill.Price,
            LastFillQty: fill.Qty,
            Commission: 0m, // gross at emit; commissionAndFeesReport is a flagged follow-up
            Status: filled ? OrderStatus.Filled : OrderStatus.PartiallyFilled,
            TransactionTime: ToTransactionTime(fill.TimeUnixSec),
            Type: ctx.Type,
            OriginalQuantity: ctx.OriginalQuantity));

        if (filled)
            Prune(fill.OrderId); // terminal — free the per-order maps
    }

    private void EmitCancel(int orderId)
    {
        // Atomic remove = exactly-once + self-pruning: a repeat cancel (or a cancel after a full fill) finds
        // nothing and is a no-op, and an external order we never placed is absent too, so it is ignored.
        if (!_orderInfo.TryRemove(orderId, out var ctx))
            return;
        _cumulativeFilled.TryRemove(orderId, out _);

        _onReport(new ExecutionReport(
            OrderId: orderId,
            Symbol: ctx.Symbol,
            Side: ctx.Side,
            ExecType: ExecType.Canceled,
            LastFillPrice: 0m,
            LastFillQty: 0m,
            Commission: 0m,
            Status: OrderStatus.Cancelled,
            TransactionTime: DateTimeOffset.UtcNow,
            Type: ctx.Type,
            OriginalQuantity: ctx.OriginalQuantity));
    }

    private void Prune(int orderId)
    {
        _orderInfo.TryRemove(orderId, out _);
        _cumulativeFilled.TryRemove(orderId, out _);
    }

    // Only called for fills from orders not in the per-order map (reconnect replay / external orders).
    // Parses IB's "BOT"/"BUY" or "SLD"/"SELL" strings; defaults to Buy when unrecognised.
    private static OrderSide ParseIbSide(string ibSide)
    {
        if (ibSide is { Length: > 0 } && ibSide.StartsWith("S", StringComparison.OrdinalIgnoreCase))
            return OrderSide.Sell;
        return OrderSide.Buy;
    }

    private static DateTimeOffset ToTransactionTime(long unixSec) =>
        unixSec > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixSec) : DateTimeOffset.UtcNow;

    public async ValueTask DisposeAsync()
    {
        // Complete the lane so the worker drains remaining events, then await it. The CTS is a hard-stop backstop
        // (a wedged _onReport): cancel only after the drain has had its chance to finish.
        _lane.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
