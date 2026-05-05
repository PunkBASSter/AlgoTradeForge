namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>Time-bar (OHLCV) subscription.</summary>
public sealed record TimeBarSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role,
    TimeFrame TimeFrame)
    : DataFeedSubscription(AssetName, Exchange, Role);
