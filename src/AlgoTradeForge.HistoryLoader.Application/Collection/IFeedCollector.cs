namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public interface IFeedCollector
{
    string FeedName { get; }
    bool SupportsSpot { get; }

    Task Collect(
        CollectionAsset asset,
        CollectionFeed feed,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct = default);
}
