using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ITakerVolumeFetcher
{
    IAsyncEnumerable<FeedRecord> FetchTakerVolumeAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}
