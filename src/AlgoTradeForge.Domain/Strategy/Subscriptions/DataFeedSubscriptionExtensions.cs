using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Bridges the polymorphic <see cref="DataFeedSubscription"/> hierarchy (TRD §9.2) to the
/// flat <see cref="DataFeedKind"/> enum used by the loader/path-resolution layer (TRD §9.5).
/// </summary>
/// <remarks>
/// Lives in <c>Domain.Strategy.Subscriptions</c> rather than <c>Domain.History</c> so that
/// <c>History</c> stays decoupled from strategy-side types — only the subscriptions layer
/// (which knows about both) needs to perform the bridging.
/// </remarks>
public static class DataFeedSubscriptionExtensions
{
    /// <summary>
    /// Returns the <see cref="DataFeedKind"/> matching this subscription's concrete subtype.
    /// </summary>
    public static DataFeedKind KindOf(this DataFeedSubscription subscription) => subscription switch
    {
        TimeBarSubscription => DataFeedKind.TimeBar,
        AltBarSubscription => DataFeedKind.AltBar,
        TickSubscription => DataFeedKind.Tick,
        SideFeedSubscription => DataFeedKind.Side,
        _ => throw new ArgumentOutOfRangeException(
            nameof(subscription),
            $"Unknown DataFeedSubscription subtype: {subscription.GetType().Name}"),
    };
}
