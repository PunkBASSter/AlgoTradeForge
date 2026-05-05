namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Alt-bar subscription (e.g. <c>EqV_1m_500m</c>). FeedId grammar validation lives at the
/// API boundary — keeps Domain free of <c>HistoryLoader.Domain</c> dependency.
/// </summary>
public sealed record AltBarSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role,
    string FeedId)
    : DataFeedSubscription(AssetName, Exchange, Role);
