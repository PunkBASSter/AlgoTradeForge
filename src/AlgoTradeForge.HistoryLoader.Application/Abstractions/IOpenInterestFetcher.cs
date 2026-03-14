using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IOpenInterestFetcher
{
    IAsyncEnumerable<FeedRecord> FetchOpenInterestAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}
