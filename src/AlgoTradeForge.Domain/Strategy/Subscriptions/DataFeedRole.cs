namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Distinguishes the strategy's primary bar feed from auxiliary side feeds in a
/// <see cref="DataFeedSubscription"/> set (TRD §9.2).
/// </summary>
/// <remarks>
/// The Primary drives the engine's bar clock (every <c>OnBarComplete</c> fires per Primary bar);
/// Side feeds are queried via <c>IFeedContext.TryGetLatest</c> at the current Primary timestamp.
/// Optimization fan-out (<c>BacktestInputs.PrimaryCandidates</c>, TRD §9.6) varies the Primary
/// per child run; Side feeds stay fixed across the fan-out.
/// </remarks>
public enum DataFeedRole
{
    /// <summary>Drives the bar clock. Exactly one per <c>BacktestInputs</c>.</summary>
    Primary,

    /// <summary>Auxiliary feed pulled from <c>IFeedContext</c>. Zero or more per <c>BacktestInputs</c>.</summary>
    Side,
}
