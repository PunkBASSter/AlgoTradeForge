namespace AlgoTradeForge.HistoryLoader.Domain;

public static class AssetPathConvention
{
    public static string DirectoryName(string symbol, string assetType) =>
        assetType.ToLowerInvariant() switch
        {
            AssetTypes.Perpetual or AssetTypes.Future => $"{symbol}_perp",
            AssetTypes.Spot or AssetTypes.Equity      => symbol,
            _ => throw new ArgumentException($"Unknown asset type: {assetType}", nameof(assetType))
        };
}
