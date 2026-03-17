namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>
/// Well-known asset type identifiers used in collection configuration.
/// </summary>
public static class AssetTypes
{
    public const string Spot = "spot";
    public const string Perpetual = "perpetual";
    public const string Future = "future";
    public const string Equity = "equity";

    public static readonly string[] All = [Spot, Perpetual, Future, Equity];

    public static bool IsFutures(string type) =>
        string.Equals(type, Perpetual, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Future, StringComparison.OrdinalIgnoreCase);

    public static bool IsSpot(string type) =>
        string.Equals(type, Spot, StringComparison.OrdinalIgnoreCase);
}
