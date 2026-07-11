using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

// TODO(phase3a Task 9/10): delete — endpoints move to ICollectionPlanSource.
// Stopgap adapter so endpoints / streams / the load-job worker still resolve legacy
// appsettings AssetCollectionConfig entries against the native CollectionAsset chain.
// NOT an adapter for collectors (they read the plan) — only for legacy config call sites.
internal static class LegacyAssetBridge
{
    public static CollectionAsset ToCollectionAsset(AssetCollectionConfig a)
    {
        var feeds = a.Feeds
            .Where(f => f.Enabled)
            .Select(f => new CollectionFeed(
                f.Name,
                f.Interval,
                f.Eager ? "eager" : "on-demand",
                "csv",
                f.HistoryStart ?? a.HistoryStart))
            .ToList();

        return new CollectionAsset(
            a.Exchange.ToLowerInvariant(),
            a.Symbol,
            new VenueInstrument(a.Symbol, a.Type, AssetPathConvention.DirectoryName(a.Symbol, a.Type)),
            a.DecimalDigits,
            feeds);
    }
}
