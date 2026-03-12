namespace AlgoTradeForge.Domain.Events;

/// <summary>
/// Well-known source identifiers used when emitting <see cref="IBacktestEvent"/> instances.
/// </summary>
public static class EventSources
{
    public const string Engine = "engine";
    public const string Live = "live";

    public const string TradeRegistry = "trade-registry";

    public static string Indicator(string indicatorName) => $"indicator.{indicatorName}";
}
