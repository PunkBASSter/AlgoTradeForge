using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ISpotDataFetcher
{
    IAsyncEnumerable<KlineRecord> FetchKlinesAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}
