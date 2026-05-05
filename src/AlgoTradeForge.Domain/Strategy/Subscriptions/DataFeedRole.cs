namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Distinguishes the strategy's primary bar feed from auxiliary side feeds.
/// Primary drives the bar clock; Side feeds are queried via <c>IFeedContext</c>.
/// </summary>
public enum DataFeedRole
{
    /// <summary>Drives the bar clock. Exactly one per <c>BacktestInputs</c>.</summary>
    Primary,

    /// <summary>Auxiliary feed pulled from <c>IFeedContext</c>. Zero or more per <c>BacktestInputs</c>.</summary>
    Side,
}
