using AlgoTradeForge.Application.Live;

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
}
