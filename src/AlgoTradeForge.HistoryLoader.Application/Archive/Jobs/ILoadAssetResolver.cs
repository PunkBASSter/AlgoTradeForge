namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public interface ILoadAssetResolver
{
    // Returns the configured asset or synthesizes one (resolving DecimalDigits) for unknown symbols.
    Task<AssetCollectionConfig> Resolve(string exchange, string symbol, string assetType, CancellationToken ct = default);
}
