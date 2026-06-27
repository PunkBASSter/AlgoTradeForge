namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The market-data request surface over the shared IB socket. Abstracted so IbSession's
// subscribe/reconnect orchestration is unit-testable without a real EClientSocket.
internal interface IIbMarketDataClient
{
    Task Connect(CancellationToken ct = default);
    int NextReqId(); // connection-scoped request-id source shared across all request types on the one socket
    void RequestTrades(int reqId, ResolvedIbContract contract);
    void RequestRealtimeBars(int reqId, ResolvedIbContract contract);
    void CancelTrades(int reqId);
    void CancelRealtimeBars(int reqId);
}
