using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Shared test double for IIbMarketDataSession. Captures trade and realtime-bar sinks so tests can
// inject events after Start/subscribe.
internal sealed class FakeIbSession : IIbMarketDataSession
{
    private readonly Dictionary<string, Action<IbTradeUpdate>> _tradeSinks = new();
    private readonly Dictionary<int, Action<IbRealtimeBar>> _barSinks = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? Reconnected { add { } remove { } }

    public Task Connect(CancellationToken ct = default) => Task.CompletedTask;

    public int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink)
    {
        _tradeSinks[contract.Spec.Symbol] = sink;
        _subscribed.TrySetResult();
        return _tradeSinks.Count;
    }

    public int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink)
    {
        _barSinks[contract.ConId] = sink;
        _subscribed.TrySetResult();
        return _barSinks.Count;
    }

    public void Unsubscribe(int reqId) { }

    public Task WaitForSubscription(CancellationToken ct = default) =>
        _subscribed.Task.WaitAsync(ct);

    public void PushTrade(string instrument, IbTradeUpdate update)
    {
        if (_tradeSinks.TryGetValue(instrument, out var sink))
            sink(update);
    }

    public void PushBar(int conId, IbRealtimeBar bar)
    {
        if (_barSinks.TryGetValue(conId, out var sink))
            sink(bar);
    }
}
