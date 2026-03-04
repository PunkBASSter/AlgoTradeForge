namespace AlgoTradeForge.Domain.Events;

/// <summary>
/// Well-known source identifiers used when emitting <see cref="IBacktestEvent"/> instances.
/// </summary>
public static class EventSources
{
    public const string Engine = "engine";
    public const string Live = "live";

    public static string Indicator(string indicatorName) => $"indicator.{indicatorName}";
}
