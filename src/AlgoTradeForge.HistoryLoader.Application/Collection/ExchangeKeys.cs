namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>
/// Builds keyed DI service keys from asset collection config.
/// Key format: "{exchange}-{futures|spot}" — e.g. "binance-futures", "binance-spot".
/// </summary>
public static class ExchangeKeys
{
    public static string Resolve(AssetCollectionConfig asset) =>
        asset.Type == "spot" ? Spot(asset.Exchange) : Futures(asset.Exchange);

    public static string Futures(string exchange) => $"{exchange}-futures";
    public static string Spot(string exchange) => $"{exchange}-spot";
}
