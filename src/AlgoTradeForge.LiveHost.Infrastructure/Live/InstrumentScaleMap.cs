using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Per-instrument price/qty scale for the data plane. Keyed by AssetName — the instrument
// key the dispatch/tick-router use (see SessionSnapshotBars). Replaces the single-asset
// "scale everything off the session asset" shortcut.
public static class InstrumentScaleMap
{
    public static IReadOnlyDictionary<string, ScaleContext> Build(
        IReadOnlyList<DataFeedSubscription> subscriptions)
    {
        var map = new Dictionary<string, ScaleContext>(StringComparer.Ordinal);
        foreach (var sub in subscriptions)
        {
            if (sub.Asset is null)
                throw new InvalidOperationException(
                    $"Subscription for '{sub.AssetName}' has no resolved Asset; resolve before building scales.");
            map[sub.AssetName] = new ScaleContext(sub.Asset);
        }
        return map;
    }
}
