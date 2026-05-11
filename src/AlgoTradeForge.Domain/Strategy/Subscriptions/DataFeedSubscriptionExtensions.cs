using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Bridges the polymorphic <see cref="DataFeedSubscription"/> hierarchy to the flat
/// <see cref="DataFeedKind"/> enum used by the loader/path-resolution layer.
/// </summary>
public static class DataFeedSubscriptionExtensions
{
    /// <summary>Returns the <see cref="DataFeedKind"/> matching this subscription's concrete subtype.</summary>
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
