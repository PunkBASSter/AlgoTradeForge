using System.Collections.Concurrent;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The order plane over the shared IB socket. Place allocates an id, captures per-order context, places, and
// awaits the first broker ack. Fills arrive on the EReader pump thread; the sink only TryWrites them onto a
// bounded lane (never blocks the pump). A single worker drains the lane, joins each fill to its captured order
// context, and emits the neutral ExecutionReport off-pump. ack timeout is bounded so a lost order can't hang.
internal sealed class IbOrderGateway : IIbOrderGateway, IAsyncDisposable
{
    private readonly record struct OrderContext(Asset Asset, OrderSide Side, OrderType Type, decimal OriginalQuantity);

    private readonly IIbOrderClient _client;
    private readonly IbWrapper _wrapper;
    private readonly Action<ExecutionReport> _onReport;
    private readonly ILogger _logger;
    private readonly TimeSpan _ackTimeout;

    private readonly ConcurrentDictionary<int, OrderContext> _orderInfo = new();
    private readonly ConcurrentDictionary<int, string> _latestStatus = new();
    private readonly Channel<IbFill> _lane;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public IbOrderGateway(IIbOrderClient client, IbWrapper wrapper, Action<ExecutionReport> onReport,
        ILogger logger, int laneCapacity = 4096, TimeSpan? ackTimeout = null)
    {
        _client = client;
        _wrapper = wrapper;
        _onReport = onReport;
        _logger = logger;
        _ackTimeout = ackTimeout ?? TimeSpan.FromSeconds(10);
        _lane = Channel.CreateBounded<IbFill>(new BoundedChannelOptions(laneCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        // Pump-thread sink: track the latest status for fidelity; never block — TryWrite the fill onto the lane.
        _wrapper.RegisterOrderSink(
            onStatus: s => _latestStatus[s.OrderId] = s.Status,
            onFill: OnFillFromPump);
        _worker = Task.Run(DrainLane);
    }

    public async Task<long> Place(string account, Asset asset, ResolvedIbContract contract, IbOrderRequest request,
        OrderSide side, OrderType type, decimal originalQuantity, CancellationToken ct = default)
    {
        var id = _client.NextOrderId();
        _orderInfo[id] = new OrderContext(asset, side, type, originalQuantity);
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

    private void OnFillFromPump(IbFill fill)
    {
        if (_lane.Writer.TryWrite(fill)) return;
        _logger.LogCritical("IB order-event lane full ({OrderId}/{ExecId}); fill dropped — execution report lost.",
            fill.OrderId, fill.ExecId);
    }

    private async Task DrainLane()
    {
        try
        {
            await foreach (var fill in _lane.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try { EmitReport(fill); }
                catch (Exception ex) when (!IsTrueShutdown(ex))
                {
                    _logger.LogError(ex, "Failed to emit execution report for fill {OrderId}/{ExecId}.",
                        fill.OrderId, fill.ExecId);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown */ }
    }

    private void EmitReport(IbFill fill)
    {
        _orderInfo.TryGetValue(fill.OrderId, out var ctx);
        var side = ResolveSide(fill.Side, ctx.Side);
        // Unmapped fill (reconnect replay / external order): asset is null and OriginalQuantity 0; the
        // dispatcher re-stamps Asset from the session, so emit with what we have rather than dropping.
        var status = MapStatus(_latestStatus.GetValueOrDefault(fill.OrderId));
        var report = new ExecutionReport(
            OrderId: fill.OrderId,
            Asset: ctx.Asset!,
            Side: side,
            ExecType: ExecType.Trade,
            LastFillPrice: (decimal)fill.Price,
            LastFillQty: fill.Qty,
            Commission: 0m, // gross at emit; commissionAndFeesReport is a flagged follow-up
            Status: status,
            TransactionTime: ToTransactionTime(fill.TimeUnixSec),
            Type: ctx.Type,
            OriginalQuantity: ctx.OriginalQuantity);
        _onReport(report);
    }

    // The per-order map's side is authoritative (the order's intent); the IB side string ("BOT"/"SLD" from
    // Execution.Side, "BUY"/"SELL" on some paths) is the fallback for an unmapped order.
    private static OrderSide ResolveSide(string ibSide, OrderSide stored)
    {
        if (ibSide is { Length: > 0 })
        {
            if (ibSide.StartsWith("B", StringComparison.OrdinalIgnoreCase)) return OrderSide.Buy;
            if (ibSide.StartsWith("S", StringComparison.OrdinalIgnoreCase)) return OrderSide.Sell;
        }
        return stored;
    }

    private static OrderStatus MapStatus(string? ibStatus) => ibStatus switch
    {
        "Filled" => OrderStatus.Filled,
        "PartiallyFilled" or "PreSubmitted" or "Submitted" => OrderStatus.PartiallyFilled,
        _ => OrderStatus.Filled, // an execDetails without a known status still means a trade happened
    };

    private static DateTimeOffset ToTransactionTime(long unixSec) =>
        unixSec > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixSec) : DateTimeOffset.UtcNow;

    private bool IsTrueShutdown(Exception ex) =>
        ex is OperationCanceledException oce && _cts.IsCancellationRequested && oce.CancellationToken == _cts.Token;

    public async ValueTask DisposeAsync()
    {
        // Complete the lane so the worker drains remaining fills, then await it. The CTS is a hard-stop backstop
        // (a wedged _onReport): cancel only after the drain has had its chance to finish.
        _lane.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
