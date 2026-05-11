namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>Tick subscription. The type alone identifies the storage path; no further payload.</summary>
public sealed record TickSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role)
    : DataFeedSubscription(AssetName, Exchange, Role);
