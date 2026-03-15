using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed class DelegatingFeedFetcher(
    Func<string, string?, long, long, CancellationToken, IAsyncEnumerable<FeedRecord>> fetch)
    : IFeedFetcher
{
    public IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string? interval, long fromMs, long toMs, CancellationToken ct)
        => fetch(symbol, interval, fromMs, toMs, ct);
}
