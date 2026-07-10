using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public static class KeepFeeds
{
    public static List<(string FeedName, string Interval)> Derive(FeedMetadata manifest)
    {
        var result = new List<(string FeedName, string Interval)>();
        foreach (var interval in manifest.Candles?.Intervals ?? [])
        {
            result.Add((FeedNames.Candles, interval));
            // candle-ext is co-written per CANDLE interval, but manifest.Feeds holds a
            // single entry whose Interval is just the last EnsureSchema write (verified:
            // CandleFeedCollector.cs:45 / KlinesArchiveMaterializer.cs:144 call it per
            // interval). Mirror candles' intervals — deriving from the manifest entry
            // would index one interval while the incremental path indexes all of them,
            // breaking the rebuild ≡ incremental invariant.
            if (manifest.Feeds.ContainsKey(FeedNames.CandleExt))
                result.Add((FeedNames.CandleExt, interval));
        }
        foreach (var (feedName, def) in manifest.Feeds)
        {
            if (feedName == FeedNames.CandleExt) continue;   // handled above, per candle interval
            result.Add((feedName, def.Interval ?? ""));
        }
        foreach (var feed in new[] { FeedNames.Ticks, FeedNames.FundingRate })
            if (!result.Any(k => k.FeedName == feed)) result.Add((feed, ""));
        return result;
    }
}
