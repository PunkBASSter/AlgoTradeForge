using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// Read-side view of configured assets joined with per-asset <c>feeds.json</c>. Cached with a
/// 30s TTL; <c>ISchemaManager.ManifestChanged</c> invalidates entries event-driven.
/// </summary>
public interface IFeedCatalog
{
    ExchangeListResponse GetExchanges();
    AssetListResponse GetAssetsByExchange(string exchange);
    AssetListResponse GetAllAssets();
    AssetCatalogEntry? GetAsset(string exchange, string assetSymbol);
    FeedDefinition? GetFeed(string exchange, string assetSymbol, string feedId);
}
