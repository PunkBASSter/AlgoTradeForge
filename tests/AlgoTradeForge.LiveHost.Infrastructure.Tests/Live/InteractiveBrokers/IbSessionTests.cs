using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbSessionTests
{
    private sealed class FakeClient : IIbMarketDataClient
    {
        public int Connects { get; private set; }
        public List<(int ReqId, string Kind)> Requests { get; } = [];
        public Action? OnConnectAttempted { get; set; }
        private readonly Queue<bool> _connectShouldThrow = new();
        private int _nextReqId;

        public void EnqueueConnectThrow() => _connectShouldThrow.Enqueue(true);

        public int NextReqId() => Interlocked.Increment(ref _nextReqId);

        public Task Connect(CancellationToken ct = default)
        {
            Connects++;
            var shouldThrow = _connectShouldThrow.Count > 0 && _connectShouldThrow.Dequeue();
            OnConnectAttempted?.Invoke();
            if (shouldThrow) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }

        public void RequestTrades(int reqId, ResolvedIbContract c) => Requests.Add((reqId, "trades"));
        public void RequestRealtimeBars(int reqId, ResolvedIbContract c) => Requests.Add((reqId, "bars"));
        public void CancelTrades(int reqId) { }
        public void CancelRealtimeBars(int reqId) { }
    }

    private static ResolvedIbContract Aapl() =>
        new(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), 265598, "AAPL", "");

    [Fact]
    public void SubscribeTrades_IssuesRequest_AndRoutesTicks()
    {
        var client = new FakeClient();
        var wrapper = new IbWrapper();
        var session = new IbSession(client, wrapper, NullLogger<IbSession>.Instance);

        IbTradeUpdate? seen = null;
        var reqId = session.SubscribeTrades(Aapl(), u => seen = u);

        Assert.Single(client.Requests);
        Assert.Equal((reqId, "trades"), client.Requests[0]);

        // wrapper callback flows to the sink the session registered
        wrapper.tickByTickAllLast(reqId, 1, 1700L, 1.0, 2m, new IBApi.TickAttribLast(), "", "");
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task ConnectionDropped_Reconnects_AndResubscribesAll_ThenRaisesReconnected()
    {
        var client = new FakeClient();
        var wrapper = new IbWrapper();
        await using var session = new IbSession(client, wrapper, NullLogger<IbSession>.Instance);

        session.SubscribeTrades(Aapl(), _ => { });
        session.SubscribeRealtimeBars(Aapl(), _ => { });
        client.Requests.Clear();

        // Reconnect is async (worker task) — await the Reconnected event to observe completion deterministically.
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Reconnected += () => reconnected.TrySetResult();

        wrapper.connectionClosed(); // drop -> signals the worker
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Connects);                 // re-connected
        Assert.Equal(2, client.Requests.Count);           // both re-issued
        Assert.Contains(client.Requests, r => r.Kind == "trades");
        Assert.Contains(client.Requests, r => r.Kind == "bars");
    }

    [Fact]
    public async Task WorkerSurvivesTransientConnectFailure_AndReconnectsOnNextDrop()
    {
        var client = new FakeClient();
        var wrapper = new IbWrapper();
        await using var session = new IbSession(client, wrapper, NullLogger<IbSession>.Instance);

        session.SubscribeTrades(Aapl(), _ => { });
        session.SubscribeRealtimeBars(Aapl(), _ => { });
        client.Requests.Clear();

        // Wire Reconnected TCS — completes only on a SUCCESSFUL reconnect.
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Reconnected += () => reconnected.TrySetResult();

        // First drop: Connect will throw — worker must survive and NOT raise Reconnected.
        // ConnectAttempted fires after each Connect call so we can sequence deterministically.
        var firstConnectAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnConnectAttempted = () => firstConnectAttempted.TrySetResult();

        client.EnqueueConnectThrow();
        wrapper.connectionClosed();

        // Wait until the worker has actually attempted (and failed) the first reconnect before
        // firing the second drop. This avoids racing against the bounded channel's DropWrite mode,
        // which would silently discard the second signal if the first drop is still in-flight.
        await firstConnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        client.OnConnectAttempted = null;

        // Second drop: Connect succeeds — Reconnected MUST fire and re-issue subscriptions.
        wrapper.connectionClosed();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(client.Connects >= 2);                // at minimum both Connect calls were made
        Assert.Equal(2, client.Requests.Count);           // re-issued after the successful reconnect
        Assert.Contains(client.Requests, r => r.Kind == "trades");
        Assert.Contains(client.Requests, r => r.Kind == "bars");
    }
}
