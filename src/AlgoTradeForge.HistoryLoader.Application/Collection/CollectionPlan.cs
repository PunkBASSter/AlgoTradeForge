using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed record CollectionFeed(string FeedName, string Interval, string Collect, string Format, DateOnly EffectiveStart);

public sealed record CollectionAsset(string Exchange, string Canonical, VenueInstrument Venue, int DecimalDigits, IReadOnlyList<CollectionFeed> Feeds);

/// <param name="Dir">Evaluator keys the blocked set by (Exchange, Dir).</param>
public sealed record BlockedAsset(string Exchange, string Canonical, string Dir, string Reason);

public sealed record PlanWarning(string Exchange, string Dir, string Message);

public sealed record CollectionPlan(
    IReadOnlyList<CollectionAsset> Assets,
    IReadOnlyList<BlockedAsset> Blocked,
    IReadOnlyList<PlanWarning> Warnings)
{
    public static readonly CollectionPlan Empty = new([], [], []);
}
