using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.Live;

public interface ILiveSessionDataProvider
{
    LiveSessionSnapshot? GetSnapshot(Guid sessionId);

    /// <summary>
    /// Fetches recent candles from the exchange REST API to fill the gap
    /// between historical CSV data and live WebSocket bars.
    /// </summary>
    Task<IReadOnlyList<Int64Bar>> GetRecentKlinesAsync(
        Guid sessionId, string symbol, string interval, decimal tickSize,
        int limit = 500, CancellationToken ct = default);
}
