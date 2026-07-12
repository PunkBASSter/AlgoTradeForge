using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

/// <summary>One desired physical feed. FeedKey = (FeedName, Interval) matches the index vocabulary:
/// candles carry one row per interval; interval-less feeds use "". Derived feeds carry their
/// derived id as FeedName (e.g. "EqV_1m_1k") — phase 3 materializes them.</summary>
public sealed record DesiredTuple(
    string Exchange, string Canonical, VenueInstrument? Venue,
    string FeedName, string Interval,
    string Collect,          // eager | on-demand (derived: materialize value)
    string Format,           // csv | parquet
    string HistoryStart,     // yyyy-MM (min across groups)
    bool IsDerived,          // interval-less collected feeds also have Interval == "" — this flag is the ONLY derived marker
    IReadOnlyList<string> Groups,  // contributing group names, for diagnostics
    string? DerivedSource = null); // source feed name for derived feeds (propagated from GroupDerived.Source)
