using AlgoTradeForge.Domain;

namespace AlgoTradeForge.Application;

public static class AssetLookupName
{
    public static string From(Asset asset) => asset switch
    {
        CryptoPerpetualAsset or FutureAsset => $"{asset.Name}_PERP",
        _ => asset.Name
    };
}
