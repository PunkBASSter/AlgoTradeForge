namespace AlgoTradeForge.HistoryLoader.Domain;

public static class AssetPathConvention
{
    public static string DirectoryName(string symbol, string assetType) => assetType switch
    {
        "perpetual" or "future" => $"{symbol}_fut",
        "spot" or "equity"     => symbol,
        _ => throw new ArgumentException($"Unknown asset type: {assetType}", nameof(assetType))
    };
}
