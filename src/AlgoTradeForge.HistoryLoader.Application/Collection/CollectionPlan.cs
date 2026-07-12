using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed record CollectionFeed(string FeedName, string Interval, string Collect, string Format, DateOnly EffectiveStart);

public sealed record CollectionAsset(string Exchange, string Canonical, VenueInstrument Venue, int DecimalDigits, IReadOnlyList<CollectionFeed> Feeds);

/// <param name="Dir">Evaluator keys the blocked set by (Exchange, Dir).</param>
public sealed record BlockedAsset(string Exchange, string Canonical, string Dir, string Reason);

public sealed record PlanWarning(string Exchange, string Dir, string Message);

// A feed that is produced by materializing a source feed rather than collected directly.
public sealed record DerivedFeedEntry(
    string Exchange, string Canonical, VenueInstrument Venue,
    string FeedName, string DerivedSource);

public sealed record CollectionPlan(
    IReadOnlyList<CollectionAsset> Assets,
    IReadOnlyList<BlockedAsset> Blocked,
    IReadOnlyList<PlanWarning> Warnings)
{
    // Derived feeds excluded from the collected Assets list; populated by CollectionPlanBuilder.
    public IReadOnlyList<DerivedFeedEntry> Derived { get; init; } = [];

    public static readonly CollectionPlan Empty = new([], [], []);
}
