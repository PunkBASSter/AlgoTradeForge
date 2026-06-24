using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Collection;

/// <summary>
/// Validates that each required strategy subscription is backed by a collected root feed.
/// Matching ignores Role; alt-bars resolve to their source root via AltBarFeedId.
/// </summary>
public static class CollectionCoverage
{
    public static string? FindUnmet(
        IReadOnlyList<DataFeedSubscription> collected,
        IEnumerable<DataFeedSubscription> required)
    {
        foreach (var r in required)
        {
            if (!IsSatisfied(collected, r))
                return Describe(r);
        }
        return null;
    }

    private static bool IsSatisfied(IReadOnlyList<DataFeedSubscription> collected, DataFeedSubscription r) => r switch
    {
        TickSubscription => collected.OfType<TickSubscription>().Any(c => SameAsset(c, r)),
        TimeBarSubscription tb => collected.OfType<TimeBarSubscription>().Any(c => SameAsset(c, r) && c.TimeFrame == tb.TimeFrame),
        SideFeedSubscription sf => collected.OfType<SideFeedSubscription>().Any(c => SameAsset(c, r) && c.FeedId == sf.FeedId),
        AltBarSubscription ab => AltBarRootSatisfied(collected, ab),
        _ => false,
    };

    private static bool AltBarRootSatisfied(IReadOnlyList<DataFeedSubscription> collected, AltBarSubscription ab)
    {
        // "ticks" source code means the alt-bar is built from raw tick data.
        if (!AltBarFeedId.TryParse(ab.FeedId, out var parsed, out _))
            return false;
        var source = parsed!.SourceCode;
        return source == "ticks"
            ? collected.OfType<TickSubscription>().Any(c => SameAsset(c, ab))
            : collected.OfType<TimeBarSubscription>().Any(c => SameAsset(c, ab) && c.TimeFrame.Code == source);
    }

    private static bool SameAsset(DataFeedSubscription a, DataFeedSubscription b) =>
        string.Equals(a.AssetName, b.AssetName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Exchange, b.Exchange, StringComparison.OrdinalIgnoreCase);

    private static string Describe(DataFeedSubscription r) => r switch
    {
        TimeBarSubscription tb => $"{r.AssetName}@{r.Exchange} time-bar {tb.TimeFrame.Code}",
        AltBarSubscription ab => AltBarFeedId.TryParse(ab.FeedId, out var parsed, out _)
            ? $"{r.AssetName}@{r.Exchange} alt-bar {ab.FeedId} (root '{parsed!.SourceCode}')"
            : $"{r.AssetName}@{r.Exchange} alt-bar {ab.FeedId} (root '(unparseable feed-id)')",
        SideFeedSubscription sf => $"{r.AssetName}@{r.Exchange} side feed '{sf.FeedId}'",
        _ => $"{r.AssetName}@{r.Exchange} {r.KindOf()}",
    };
}
