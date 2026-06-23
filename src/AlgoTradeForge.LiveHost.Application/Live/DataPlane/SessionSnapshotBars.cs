using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

// Assembles a session snapshot's bar fields from the shared bar sources' Recent rings.
// Pure given a recentBars lookup, so it is unit-testable without a live connector.
public static class SessionSnapshotBars
{
    public sealed record Result(
        IReadOnlyList<Int64Bar> Bars,
        IReadOnlyList<SubscriptionLastBar> LastBarsPerSubscription);

    // recentBars((instrument, spec)) returns the source's Recent (empty when none).
    public static Result Build(
        IReadOnlyList<DataFeedSubscription> subscriptions,
        Func<string, BarSpecKey, IReadOnlyList<Int64Bar>> recentBars)
    {
        var lastBars = new List<SubscriptionLastBar>();
        IReadOnlyList<Int64Bar> primaryBars = [];

        for (var i = 0; i < subscriptions.Count; i++)
        {
            var sub = subscriptions[i];
            var spec = SpecFor(sub);
            if (spec is null) continue; // Tick / unknown -> no bars

            var instrument = sub.AssetName;
            var recent = recentBars(instrument, spec.Value);
            if (recent.Count == 0) continue;

            // Flat Bars list = subscription[0]'s bars — the PRIMARY GetLiveSessionDataQuery backfills
            // its REST klines against and dedups with by TimestampMs. Fall back to the first bar
            // subscription that has data when [0] is a tick sub (no bars of its own).
            if (i == 0 || primaryBars.Count == 0)
                primaryBars = recent;

            lastBars.Add(new SubscriptionLastBar(sub, recent[^1]));
        }

        return new Result(primaryBars, lastBars);
    }

    private static BarSpecKey? SpecFor(DataFeedSubscription raw) => raw switch
    {
        TimeBarSubscription tb => BarSpecKey.TimeBar(tb.TimeFrame),
        AltBarSubscription ab => BarSpecKey.AltBar(ab.FeedId),
        _ => null,
    };
}
