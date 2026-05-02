namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Time-bar (OHLCV) subscription. Resolves to <c>candles/&lt;YYYY-MM&gt;_{TimeFrame}.csv</c>
/// at the loader (TRD §9.3, §9.5).
/// </summary>
public sealed record TimeBarSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role,
    TimeFrame TimeFrame)
    : DataFeedSubscription(AssetName, Exchange, Role);
