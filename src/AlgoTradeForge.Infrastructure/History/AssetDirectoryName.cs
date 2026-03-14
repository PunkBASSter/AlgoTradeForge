using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.Infrastructure.History;

public static class AssetDirectoryName
{
    public static string From(Asset asset) => asset switch
    {
        CryptoPerpetualAsset a => AssetPathConvention.DirectoryName(a.Name, "perpetual"),
        FutureAsset a          => AssetPathConvention.DirectoryName(a.Name, "future"),
        CryptoAsset a          => AssetPathConvention.DirectoryName(a.Name, "spot"),
        EquityAsset a          => AssetPathConvention.DirectoryName(a.Name, "equity"),
        _ => throw new ArgumentException($"Unknown asset type: {asset.GetType().Name}")
    };
}
