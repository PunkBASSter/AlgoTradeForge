using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// Read-side view of configured assets joined with per-asset <c>feeds.json</c> under DataRoot.
/// Cached until <c>ISchemaManager.ManifestChanged</c> fires or the 10-min TTL expires.
/// </summary>
public interface IFeedCatalog
{
    Task<ExchangeListResponse> GetExchanges(CancellationToken ct = default);
    Task<AssetListResponse> GetAssetsByExchange(string exchange, CancellationToken ct = default);
    Task<AssetListResponse> GetAllAssets(CancellationToken ct = default);
    Task<AssetCatalogEntry?> GetAsset(string exchange, string assetSymbol, CancellationToken ct = default);
    Task<FeedDefinition?> GetFeed(string exchange, string assetSymbol, string feedId, CancellationToken ct = default);
}
