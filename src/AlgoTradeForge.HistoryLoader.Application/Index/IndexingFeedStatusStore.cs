using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexingFeedStatusStore(IFeedStatusStore inner, IIndexMaintenance maintenance) : IFeedStatusStore
{
    public Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default) =>
        inner.Load(assetDir, feedName, interval, ct);

    public async Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default)
    {
        await inner.Save(assetDir, feedName, interval, status, ct);
        maintenance.Enqueue(new IndexWork.FeedTouched(assetDir, feedName, interval));
    }
}
