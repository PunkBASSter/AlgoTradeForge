using System.Collections.Concurrent;
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

    public Task<int> NextValidId => _nextValidId.Task;
    public event Action? ConnectionDropped;

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
}
