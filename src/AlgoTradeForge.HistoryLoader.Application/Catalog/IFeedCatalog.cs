using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// Read-side aggregation of <c>HistoryLoaderOptions.Assets</c> + per-asset <c>feeds.json</c>
/// (TRD §5.1). Cached with a 30s TTL fallback; event-driven invalidation on
/// <c>ISchemaManager.ManifestChanged</c> keeps responses fresh without polling. Phase 3's
/// main-API proxy is the primary downstream consumer.
/// </summary>
public interface IFeedCatalog
{
    ExchangeListResponse GetExchanges();
    AssetListResponse GetAssetsByExchange(string exchange);
    AssetListResponse GetAllAssets();

    /// <summary>Returns the catalog entry for one asset, or <c>null</c> when not configured.</summary>
    AssetCatalogEntry? GetAsset(string exchange, string assetSymbol);

    /// <summary>
    /// Returns the raw <see cref="FeedDefinition"/> for one feed, or <c>null</c>. Used by the
    /// status endpoint and the eligibility resolver.
    /// </summary>
    FeedDefinition? GetFeed(string exchange, string assetSymbol, string feedId);
}
