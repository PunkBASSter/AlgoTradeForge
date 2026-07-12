namespace AlgoTradeForge.HistoryLoader.Application.Groups;

// Status values: unsupported | on-demand | blocked | awaiting-data | missing | partial | materialized
public sealed record TupleStatus(DesiredTuple Tuple, string Status, int MonthsExpected, int MonthsCovered);

public sealed record OrphanEntry(string Exchange, string Dir, string FeedName, string Interval);

public sealed record ConvergenceReport(
    DateTimeOffset ComputedAt,
    IReadOnlyList<TupleStatus> Tuples,
    IReadOnlyList<OrphanEntry> Orphaned,      // indexed feeds no enabled group references (spec §3.4: NEVER auto-deleted)
    IReadOnlyList<GroupConflict> Conflicts);
