using System.Collections.Concurrent;
using System.Globalization;
using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Derives from DefaultEWrapper so only the callbacks Plan 1 exercises are overridden; every other EWrapper
// member (incl. 10.45 ProtoBuf variants) inherits an empty body. Accumulates contractDetails per reqId and
// completes the awaiter on contractDetailsEnd (a single reqContractDetails returns many months for a futures
// family). Callbacks fire on the single EReader pump thread, so per-reqId accumulation is not concurrent.
// Plan 3/4 grow this with tick / order / fill callbacks.
internal sealed class IbWrapper : DefaultEWrapper
{
    private sealed class Pending
    {
        public List<IbContractDetailsResult> Items { get; } = [];
        public TaskCompletionSource<IReadOnlyList<IbContractDetailsResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private TaskCompletionSource<int> _nextValidId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Pending> _byReq = new();
    private readonly ConcurrentDictionary<int, Action<IbTradeUpdate>> _tickSinks = new();
    private readonly ConcurrentDictionary<int, Action<IbRealtimeBar>> _barSinks = new();
    private readonly ConcurrentDictionary<int, (List<IbHistoricalTick> Items, TaskCompletionSource<IReadOnlyList<IbHistoricalTick>> Tcs)> _histByReq = new();

    private Action<IbOrderStatusUpdate>? _onStatus;
    private Action<IbFill>? _onFill;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IbOrderStatusUpdate>> _acks = new();
    private readonly HashSet<string> _seenExecIds = new();
    private readonly Queue<string> _execIdOrder = new();
    private const int ExecDedupCapacity = 4096;
    private List<IbOpenOrder>? _openOrders;
    private TaskCompletionSource<IReadOnlyList<IbOpenOrder>>? _openOrderSnapshot;

    // IB error codes on a known ack id that mean the placement genuinely failed (fault the awaiter).
    //  201   = order rejected (risk/precautionary/exchange reject).
    //  10052 = empty TIF — a malformed-order placement reject.
    // 202 ("order cancelled") is deliberately EXCLUDED: it is a cancellation confirmation, not a submit-time
    // rejection. A placement that is immediately cancelled still acked as Submitted first; treating 202 as a
    // placement fault would race the legitimate orderStatus ack and surface spurious failures. Everything else
    // arriving on an ack id (399 order-message, 2100-2199 warnings, 10167 delayed-data, etc.) is informational
    // and must NOT fault the awaiter.
    private static readonly HashSet<int> RejectCodes = [201, 10052];

    public Task<int> NextValidId => _nextValidId.Task;
    public event Action? ConnectionDropped;
    public event Action<IbFill>? Fill;

    // A reconnect issues a fresh nextValidId; re-arm the awaiter so the new value is observed (a completed TCS can't be re-set).
    public void ResetForReconnect() => _nextValidId = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Register BEFORE issuing reqContractDetails (callbacks fire on the pump thread). The returned scope carries
    // the awaiter and evicts the reqId on Dispose; release via `using` so an abandoned request can't leak.
    public ContractDetailsRequest RegisterContractDetails(int reqId) =>
        new(this, reqId, _byReq.GetOrAdd(reqId, _ => new Pending()).Completion.Task);

    public void ReleaseContractDetails(int reqId) => _byReq.TryRemove(reqId, out _);

    public Task<IReadOnlyList<IbHistoricalTick>> RegisterHistoricalTicks(int reqId)
    {
        var entry = _histByReq.GetOrAdd(reqId, _ => ([], new(TaskCreationOptions.RunContinuationsAsynchronously)));
        return entry.Tcs.Task;
    }

    public void RegisterTickSink(int reqId, Action<IbTradeUpdate> sink) => _tickSinks[reqId] = sink;
    public void RegisterBarSink(int reqId, Action<IbRealtimeBar> sink) => _barSinks[reqId] = sink;
    public void ReleaseMarketData(int reqId) { _tickSinks.TryRemove(reqId, out _); _barSinks.TryRemove(reqId, out _); }

    // Installed once by IbOrderGateway (B4). onStatus fires on every orderStatus; onFill on each deduped fill.
    public void RegisterOrderSink(Action<IbOrderStatusUpdate> onStatus, Action<IbFill> onFill)
    {
        _onStatus = onStatus;
        _onFill = onFill;
    }

    // Completes on the first orderStatus/openOrder for orderId; faults on a reject-coded error for that id.
    public Task<IbOrderStatusUpdate> RegisterOrderAck(int orderId)
    {
        var tcs = new TaskCompletionSource<IbOrderStatusUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acks[orderId] = tcs;
        return tcs.Task;
    }

    public void ReleaseOrderAck(int orderId) => _acks.TryRemove(orderId, out _);

    // Reconnect pushback: openOrder accumulates into the snapshot, openOrderEnd completes it.
    public Task<IReadOnlyList<IbOpenOrder>> BeginOpenOrderSnapshot()
    {
        _openOrders = [];
        _openOrderSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _openOrderSnapshot.Task;
    }

    public override void nextValidId(int orderId) => _nextValidId.TrySetResult(orderId);

    public override void contractDetails(int reqId, ContractDetails contractDetails)
    {
        if (_byReq.TryGetValue(reqId, out var pending))
            pending.Items.Add(new IbContractDetailsResult(
                contractDetails.Contract.ConId,
                contractDetails.Contract.LocalSymbol,
                contractDetails.Contract.LastTradeDateOrContractMonth ?? ""));
    }

    public override void contractDetailsEnd(int reqId)
    {
        if (_byReq.TryGetValue(reqId, out var pending))
            pending.Completion.TrySetResult(pending.Items.ToArray());
    }

    public override void connectionClosed() => ConnectionDropped?.Invoke();

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // Connectivity / data-farm notices arrive with id == -1.
        //  1100 = connectivity lost: the socket is usually still alive and IB self-heals with 1102 — do NOT
        //         reconnect (reconnecting on every transient blip storms the gateway with the same clientId,
        //         which IB then rejects). If the socket actually dies, connectionClosed() drives the reconnect.
        //  1101 = connectivity restored, DATA LOST: live subscriptions must be re-issued → signal recovery.
        //         The recovery path's Connect is idempotent, so on a still-alive socket this is a re-subscribe.
        //  1102 = connectivity restored, data maintained: nothing to do.
        if (id == -1)
        {
            if (errorCode == 1101) ConnectionDropped?.Invoke();
            return;
        }

        // Order-correlated errors (id == orderId with a registered ack): fault ONLY for genuine reject codes.
        // Non-reject codes (399, 2100-2199, 10167, 10189 pacing/no-permission, …) fall through to the request
        // fault paths below — the order-id space and req-id space use separate counters and CAN overlap
        // numerically, so a market-data error on a shared numeric id must still reach _byReq/_histByReq.
        // See RejectCodes for the 202 exclusion rationale.
        if (_acks.TryGetValue(id, out var ackTcs) && RejectCodes.Contains(errorCode))
        {
            ackTcs.TrySetException(new IbRequestException(errorCode, errorMsg));
            return;
        }

        // Request-correlated errors (id >= 0): fault whichever pending awaiter owns this reqId so the caller
        // fails loud (IbRequestException) instead of blocking to its timeout. A reqHistoricalTicks error
        // (10189 no-permission, no-data, pacing) arrives here and MUST fault _histByReq, or the bar source's
        // recovery wedges on the 30s timeout.
        if (_byReq.TryGetValue(id, out var pending))
            pending.Completion.TrySetException(new IbRequestException(errorCode, errorMsg));
        else if (_histByReq.TryRemove(id, out var hist))
            hist.Tcs.TrySetException(new IbRequestException(errorCode, errorMsg));
    }

    public override void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)
    {
        if (!_histByReq.TryGetValue(reqId, out var entry)) return;
        foreach (var t in ticks) entry.Items.Add(new IbHistoricalTick(t.Time, t.Price, t.Size));
        if (done && _histByReq.TryRemove(reqId, out var finished))
            finished.Tcs.TrySetResult(finished.Items.ToArray());
    }

    public override void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
        TickAttribLast tickAttribLast, string exchange, string specialConditions)
    {
        if (_tickSinks.TryGetValue(reqId, out var sink))
            sink(new IbTradeUpdate(time, price, size));
    }

    public override void realtimeBar(int reqId, long time, double open, double high, double low, double close,
        decimal volume, decimal WAP, int count)
    {
        if (_barSinks.TryGetValue(reqId, out var sink))
            sink(new IbRealtimeBar(time, open, high, low, close, volume));
    }

    public override void orderStatus(int orderId, string status, decimal filled, decimal remaining,
        double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld,
        double mktCapPrice)
    {
        var update = new IbOrderStatusUpdate(orderId, status, filled, remaining, avgFillPrice);
        if (_acks.TryGetValue(orderId, out var tcs)) tcs.TrySetResult(update);
        _onStatus?.Invoke(update);
    }

    // Fills are GROSS here — commission arrives later via commissionAndFeesReport and is NOT joined at emit.
    public override void execDetails(int reqId, Contract contract, Execution execution)
    {
        if (!MarkExecSeen(execution.ExecId)) return; // reconnect replays the same execId; apply once
        var fill = new IbFill(execution.OrderId, execution.ExecId, execution.Price, execution.Shares,
            execution.Side, ParseExecTime(execution.Time));
        _onFill?.Invoke(fill);
        Fill?.Invoke(fill);
    }

    // Commission lands AFTER execDetails; the design keeps fills gross, so a deferred Portfolio cash-adjustment
    // is a flagged follow-up. No-op here.
    public override void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport) { }

    public override void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
    {
        var snapshot = new IbOpenOrder(orderId, order.Account ?? "", contract.Symbol ?? "", order.Action ?? "",
            order.OrderType ?? "", order.TotalQuantity, order.LmtPrice, order.AuxPrice, orderState.Status ?? "");
        _openOrders?.Add(snapshot);

        // openOrder also serves as a first ack for a placement that gets an open-order push before orderStatus.
        if (_acks.TryGetValue(orderId, out var tcs))
            tcs.TrySetResult(new IbOrderStatusUpdate(orderId, orderState.Status ?? "", 0, order.TotalQuantity, 0));
    }

    public override void openOrderEnd()
    {
        _openOrderSnapshot?.TrySetResult(_openOrders ?? []);
        _openOrders = null;
        _openOrderSnapshot = null;
    }

    private bool MarkExecSeen(string execId)
    {
        lock (_execIdOrder)
        {
            if (!_seenExecIds.Add(execId)) return false;
            _execIdOrder.Enqueue(execId);
            if (_execIdOrder.Count > ExecDedupCapacity) _seenExecIds.Remove(_execIdOrder.Dequeue());
            return true;
        }
    }

    // IB exec time is a string ("yyyyMMdd  HH:mm:ss" or a "yyyyMMdd-HH:mm:ss" / Unix-seconds variant). Parse
    // best-effort to Unix seconds; on the pump thread we never throw — a 0 is recoverable (B4 re-stamps the fill).
    private static long ParseExecTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return 0;

        if (long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            return epoch;

        var normalized = string.Join(" ", time.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string[] formats =
        [
            "yyyyMMdd HH:mm:ss",
            "yyyyMMdd-HH:mm:ss",
            "yyyyMMdd HH:mm:ss zzz",
        ];
        if (DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUnixTimeSeconds();
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out dto))
            return dto.ToUnixTimeSeconds();
        return 0;
    }
}
