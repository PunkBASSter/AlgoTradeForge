namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbMarketDataClient: issues tick-by-tick + 5s realtime-bar requests on the shared socket.
// IBApi vocabulary stops here. tickType "AllLast" + barSize 5 "TRADES" mirror the POC.
internal sealed class IbConnectionMarketDataClient(IbConnection connection) : IIbMarketDataClient
{
    public Task Connect(CancellationToken ct = default) => connection.Connect(ct: ct);

    public int NextReqId() => connection.NextReqId();

    public void RequestTrades(int reqId, ResolvedIbContract contract) =>
        connection.Client.reqTickByTickData(reqId, contract.ToIbApiContract(), "AllLast", 0, false);

    public void RequestRealtimeBars(int reqId, ResolvedIbContract contract) =>
        connection.Client.reqRealTimeBars(reqId, contract.ToIbApiContract(), 5, "TRADES", false, null);

    public void CancelTrades(int reqId) => connection.Client.cancelTickByTickData(reqId);
    public void CancelRealtimeBars(int reqId) => connection.Client.cancelRealTimeBars(reqId);
}
