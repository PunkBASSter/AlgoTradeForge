using AlgoTradeForge.HistoryLoader.Application.Collection;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed record DateRange(DateOnly From, DateOnly To);

public abstract record MaterializeStage
{
    // Source feed to load; key = {exchange}|{dir}|{feedName}|{interval}
    public sealed record Load(string FeedKey) : MaterializeStage;
    // Derived output feed to aggregate into; key = {exchange}|{dir}|{derivedFeedName}|
    public sealed record Aggregate(string FeedKey) : MaterializeStage;
}

public sealed class FeedNotMaterializableException(string exchange, string symbol, string feed)
    : Exception($"Feed '{feed}' on {exchange}/{symbol} is not found in the plan or is not on-demand/derived.")
{
    public string Exchange { get; } = exchange;
    public string Symbol { get; } = symbol;
    public string Feed { get; } = feed;
}

public sealed record MaterializePlan(
    string Exchange,
    string Dir,
    string FeedName,
    DateRange? Range,
    IReadOnlyList<MaterializeStage> Stages,
    // The gate key that owns this materialize job (last stage's feed key).
    string OutputFeedKey)
{
    /// <summary>Resolves a target feed in the collection plan into 1 or 2 materialize stages.
    /// Derived feed (IsDerived) → [Load(sourceFeedKey), Aggregate(outputFeedKey)].
    /// On-demand collected feed → [Load(feedKey)].
    /// Anything else → <see cref="FeedNotMaterializableException"/>.</summary>
    public static MaterializePlan Resolve(
        CollectionPlan plan,
        string exchange,
        string symbol,
        string feed,
        DateRange? range)
    {
        var xch = exchange.ToLowerInvariant();

        // 1. Derived feeds: 2-stage plan (Load source → Aggregate output)
        var derivedEntry = plan.Derived.FirstOrDefault(d =>
            string.Equals(d.Exchange, xch, StringComparison.Ordinal) &&
            string.Equals(d.Venue.ApiSymbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.FeedName, feed, StringComparison.Ordinal));

        if (derivedEntry is not null)
        {
            var dir = derivedEntry.Venue.Dir;
            var sourceFeedKey = $"{xch}|{dir}|{derivedEntry.DerivedSource}|";
            var outputFeedKey = $"{xch}|{dir}|{feed}|";
            IReadOnlyList<MaterializeStage> stages =
                [new MaterializeStage.Load(sourceFeedKey), new MaterializeStage.Aggregate(outputFeedKey)];
            return new MaterializePlan(xch, dir, feed, range, stages, outputFeedKey);
        }

        // 2. On-demand collected feeds: 1-stage plan (Load)
        foreach (var asset in plan.Assets)
        {
            if (!string.Equals(asset.Exchange, xch, StringComparison.Ordinal)) continue;
            if (!string.Equals(asset.Venue.ApiSymbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;

            var cf = asset.Feeds.FirstOrDefault(f =>
                string.Equals(f.FeedName, feed, StringComparison.Ordinal) &&
                string.Equals(f.Collect, "on-demand", StringComparison.Ordinal));

            if (cf is not null)
            {
                var dir = asset.Venue.Dir;
                // Interval-less feeds trail an empty segment so the key shape matches the load gate.
                var feedKey = $"{xch}|{dir}|{feed}|{cf.Interval}";
                IReadOnlyList<MaterializeStage> stages = [new MaterializeStage.Load(feedKey)];
                return new MaterializePlan(xch, dir, feed, range, stages, feedKey);
            }
        }

        throw new FeedNotMaterializableException(xch, symbol, feed);
    }
}
