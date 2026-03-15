using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFeedFetcher
{
    IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string? interval, long fromMs, long toMs, CancellationToken ct);
}
