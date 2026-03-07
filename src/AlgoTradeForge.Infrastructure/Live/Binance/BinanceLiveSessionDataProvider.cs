using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceLiveSessionDataProvider(ILiveSessionStore sessionStore) : ILiveSessionDataProvider
{
    public LiveSessionSnapshot? GetSnapshot(Guid sessionId)
    {
        var details = sessionStore.Get(sessionId);
        if (details is null)
            return null;

        if (details.Connector is not BinanceLiveConnector binanceConnector)
            return null;

        return binanceConnector.GetSessionSnapshot(sessionId);
    }

    public async Task<IReadOnlyList<Int64Bar>> GetRecentKlinesAsync(
        Guid sessionId, string symbol, string interval, decimal tickSize,
        int limit = 500, CancellationToken ct = default)
    {
        var details = sessionStore.Get(sessionId);
        if (details?.Connector is not BinanceLiveConnector binanceConnector)
            return [];

        return await binanceConnector.GetRecentKlinesAsync(symbol, interval, tickSize, limit, ct);
    }
}
