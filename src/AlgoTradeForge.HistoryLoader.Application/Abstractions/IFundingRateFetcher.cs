using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFundingRateFetcher
{
    IAsyncEnumerable<FeedRecord> FetchFundingRatesAsync(
        string symbol, long fromMs, long toMs, CancellationToken ct);
}
