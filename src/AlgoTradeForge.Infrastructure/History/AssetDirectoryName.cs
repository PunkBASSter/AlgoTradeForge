using AlgoTradeForge.Domain;

namespace AlgoTradeForge.Infrastructure.History;

public static class AssetDirectoryName
{
    public static string From(Asset asset) => asset switch
    {
        CryptoPerpetualAsset a => $"{a.Name}_perp",
        FutureAsset a          => $"{a.Name}_perp",
        CryptoAsset a          => a.Name,
        EquityAsset a          => a.Name,
        _ => throw new ArgumentException($"Unknown asset type: {asset.GetType().Name}")
    };
}
