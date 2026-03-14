using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ILongShortRatioFetcher
{
    IAsyncEnumerable<FeedRecord> FetchGlobalLongShortRatioAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchTopAccountRatioAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchTopPositionRatioAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}
