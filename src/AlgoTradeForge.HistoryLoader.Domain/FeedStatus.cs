namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>
/// Healthy: collection is current. Degraded: gaps detected or partial data.
/// Error: unrecoverable I/O or API failure.
/// </summary>
public enum CollectionHealth { Healthy, Degraded, Error }

/// <summary>Both ends are present rows: FromMs = last row before the hole, ToMs = first row after it.</summary>
public readonly record struct DataGap
{
    public long FromMs { get; init; }
    public long ToMs { get; init; }
}

public sealed class FeedStatus
{
    public string FeedName { get; init; } = "";
    public string Interval { get; init; } = "";
    public long? FirstTimestamp { get; init; }
    public long? LastTimestamp { get; init; }
    public DateTimeOffset? LastRunUtc { get; init; }
    public long RecordCount { get; init; }
    public IReadOnlyList<DataGap> Gaps { get; init; } = [];
    public CollectionHealth Health { get; init; } = CollectionHealth.Healthy;

    /// <summary>Months ("yyyy-MM") materialized from a complete monthly archive zip. Coverage marker for interval-less feeds (ticks, funding-rate); NOT the row-count predicate.</summary>
    public IReadOnlyList<string> CompleteMonths { get; init; } = [];
}
