namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>
/// Classifies an on-disk <c>{exchange}/{dir}</c> asset directory into a raw symbol + type.
/// Lossy by design: a bare dir can't distinguish spot-vs-equity except by exchange, nor
/// <c>_perp</c> as perpetual-vs-future — the catalog only needs a display/filter heuristic;
/// authoritative Asset resolution happens in the main app's FileSystemAssetRepository.
/// </summary>
public static class AssetDirectoryClassifier
{
    private const string PerpSuffix = "_perp";

    private static readonly HashSet<string> UsEquityExchanges =
        new(StringComparer.OrdinalIgnoreCase) { "NASDAQ", "NYSE", "NYSEMKT", "AMEX", "ARCA", "BATS" };

    public static bool IsUsEquityExchange(string exchange) => UsEquityExchanges.Contains(exchange);

    public static (string Symbol, string Type) Classify(string exchange, string dirName)
    {
        if (dirName.EndsWith(PerpSuffix, StringComparison.OrdinalIgnoreCase))
            return (dirName[..^PerpSuffix.Length], AssetTypes.Perpetual);
        if (IsUsEquityExchange(exchange))
            return (dirName, AssetTypes.Equity);
        return (dirName, AssetTypes.Spot);
    }
}
