using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The shared per-venue IB session: owns reqId allocation over the one socket and tracks every active
// market-data subscription so a reconnect can re-issue them. The data plane (Plan 3) and the order
// plane (Plan 4) both hold a handle to this. Reconnect is FULLY ASYNC: IbWrapper.ConnectionDropped
// (raised on the EReader pump thread) only signals a bounded channel; a worker task drains it and
// reconnects off the pump thread (no sync-over-async, no blocking the dying pump). The subscription
// map is guarded by a lock (Subscribe runs on the caller thread; the worker re-issues under the lock).
internal sealed class IbSession : IIbMarketDataSession, IAsyncDisposable
{
    private abstract record Sub(int ReqId, ResolvedIbContract Contract);
    private sealed record TradeSub(int ReqId, ResolvedIbContract Contract, Action<IbTradeUpdate> Sink) : Sub(ReqId, Contract);
    private sealed record BarSub(int ReqId, ResolvedIbContract Contract, Action<IbRealtimeBar> Sink) : Sub(ReqId, Contract);

    private readonly IIbMarketDataClient _client;
    private readonly IbWrapper _wrapper;
    private readonly ILogger<IbSession> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Sub> _subs = new();
    private readonly Channel<bool> _drops = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }); // coalesce bursts to one pending reconnect
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public event Action? Reconnected;

    public IbSession(IIbMarketDataClient client, IbWrapper wrapper, ILogger<IbSession> logger)
    {
        _client = client;
        _wrapper = wrapper;
        _logger = logger;
        _wrapper.ConnectionDropped += OnConnectionDropped;
        _worker = Task.Run(ReconnectLoop);
    }

    public Task Connect(CancellationToken ct = default) => _client.Connect(ct);

    public int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink)
    {
        var reqId = _client.NextReqId();
        using (_gate.EnterScope()) _subs[reqId] = new TradeSub(reqId, contract, sink);
        _wrapper.RegisterTickSink(reqId, sink);
        _client.RequestTrades(reqId, contract);
        return reqId;
    }

    public int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink)
    {
        var reqId = _client.NextReqId();
        using (_gate.EnterScope()) _subs[reqId] = new BarSub(reqId, contract, sink);
        _wrapper.RegisterBarSink(reqId, sink);
        _client.RequestRealtimeBars(reqId, contract);
        return reqId;
    }

    public void Unsubscribe(int reqId)
    {
        Sub? sub;
        using (_gate.EnterScope()) { _subs.Remove(reqId, out sub); }
        if (sub is null) return;
        _wrapper.ReleaseMarketData(reqId);
        switch (sub) { case TradeSub: _client.CancelTrades(reqId); break; case BarSub: _client.CancelRealtimeBars(reqId); break; }
    }

    // Pump-thread callback — do NOT block here. Just signal the worker (coalesced to one pending reconnect).
    private void OnConnectionDropped() => _drops.Writer.TryWrite(true);

    private async Task ReconnectLoop()
    {
        try
        {
            await foreach (var _ in _drops.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await Reconnect(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    throw; // shutdown — exit the loop
                }
                catch (Exception ex)
                {
                    // A transient reconnect failure must NOT kill the worker — log and wait for the next drop signal.
                    _logger.LogError(ex, "IB reconnect attempt failed; will retry on the next disconnect signal.");
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown */ }
    }

    private async Task Reconnect(CancellationToken ct)
    {
        // Connect owns nextValidId re-arming (per establish attempt) and is idempotent: a real drop re-establishes;
        // a 1101 "data lost" on a still-alive socket no-ops the connect and we simply re-issue the subscriptions.
        await _client.Connect(ct).ConfigureAwait(false);

        List<Sub> active;
        using (_gate.EnterScope()) active = [.. _subs.Values];
        foreach (var sub in active)
        {
            switch (sub)
            {
                case TradeSub t: _wrapper.RegisterTickSink(t.ReqId, t.Sink); _client.RequestTrades(t.ReqId, t.Contract); break;
                case BarSub b: _wrapper.RegisterBarSink(b.ReqId, b.Sink); _client.RequestRealtimeBars(b.ReqId, b.Contract); break;
            }
        }
        Reconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _wrapper.ConnectionDropped -= OnConnectionDropped;
        _drops.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
