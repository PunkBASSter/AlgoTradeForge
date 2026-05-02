namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Tick subscription. Resolves to <c>ticks/&lt;YYYY-MM-DD&gt;.csv</c> at the loader
/// (TRD §9.3, §9.5). The type alone identifies the storage path; no further payload.
/// </summary>
public sealed record TickSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role)
    : DataFeedSubscription(AssetName, Exchange, Role);
