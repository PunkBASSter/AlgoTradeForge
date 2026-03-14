namespace AlgoTradeForge.HistoryLoader.Domain;

public static class AssetPathConvention
{
    public static string DirectoryName(string symbol, string assetType)
    {
        var suffix = assetType is "perpetual" or "future" ? "_fut" : "";
        return $"{symbol}{suffix}";
    }
}
