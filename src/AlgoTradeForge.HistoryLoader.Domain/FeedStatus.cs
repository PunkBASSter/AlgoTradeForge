namespace AlgoTradeForge.HistoryLoader.Domain;

public enum CollectionHealth { Healthy, Degraded, Error }

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
}
