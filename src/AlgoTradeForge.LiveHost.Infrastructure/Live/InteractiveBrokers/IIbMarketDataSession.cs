namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Seam over IbSession allowing the data plane to be unit-tested without a real socket.
internal interface IIbMarketDataSession
{
    Task Connect(CancellationToken ct = default);
    int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink);
    int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink);
    void Unsubscribe(int reqId);
    event Action? Reconnected;
}
