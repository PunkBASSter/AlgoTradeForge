using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

public sealed class BinanceLiveSessionDataProvider(ILiveSessionStore sessionStore) : ILiveSessionDataProvider
{
    public async Task<LiveSessionSnapshot?> GetSnapshotAsync(Guid sessionId, CancellationToken ct = default)
    {
        var details = sessionStore.Get(sessionId);
        if (details is null)
            return null;

        if (details.Connector is not BinanceLiveConnector binanceConnector)
            return null;

        return await binanceConnector.GetSessionSnapshotAsync(sessionId, ct);
    }

    // Bar delivery is deferred to Plan 4 (IStrategyDispatch data plane).
    public Task<IReadOnlyList<Int64Bar>> GetRecentKlinesAsync(
        Guid sessionId, string symbol, string interval, decimal tickSize,
        int limit = 500, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Int64Bar>>([]);
}
