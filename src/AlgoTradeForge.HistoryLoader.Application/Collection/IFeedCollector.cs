namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public interface IFeedCollector
{
    string FeedName { get; }
    bool SupportsSpot { get; }

    Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct);
}
