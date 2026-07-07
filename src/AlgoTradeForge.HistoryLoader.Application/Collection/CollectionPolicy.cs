using AlgoTradeForge.HistoryLoader.Application.Archive;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>
/// Eager/lazy collection decision (spec §1). Replenishable feeds — an archive materializer
/// exists for (exchange, feed, assetType) — default to lazy on-demand loading; per-feed
/// "Eager": true opts back in. Irreplaceable feeds are always eager: skipping them loses
/// data forever. Governs cron collectors AND stream startup symbol sets alike.
/// </summary>
public sealed class CollectionPolicy(ArchiveMaterializerRegistry registry)
{
    public bool IsEagerlyCollected(AssetCollectionConfig asset, FeedCollectionConfig feed) =>
        feed.Eager || !registry.IsReplenishable(asset.Exchange, feed.Name, asset.Type);
}
