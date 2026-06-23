using AlgoTradeForge.Domain.Strategy.Subscriptions;
using System.Collections.Generic;
using System.Linq;
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

    public static Asset RequireAsset(this DataFeedSubscription sub) =>
        sub.Asset ?? throw new InvalidOperationException(
            $"DataFeedSubscription for '{sub.AssetName}' is unresolved (Asset is null).");

    public static string FeedKey(this DataFeedSubscription sub) => sub switch
    {
        TimeBarSubscription => "ohlcv",
        AltBarSubscription ab => ab.FeedId,
        TickSubscription => "ticks",
        SideFeedSubscription sf => sf.FeedId,
        _ => throw new ArgumentOutOfRangeException(nameof(sub), sub.GetType().Name, "Unknown subscription kind"),
    };

    public static Asset ResolveExecutionAsset(this IReadOnlyList<DataFeedSubscription> subs) =>
        (subs.FirstOrDefault(s => s.Role == DataFeedRole.Primary) ?? subs[0]).RequireAsset();
}
