namespace AlgoTradeForge.HistoryLoader.State;

internal enum CollectionHealth { Healthy, Degraded, Error }

internal sealed class DataGap
{
    public long FromMs { get; init; }
    public long ToMs { get; init; }
}

internal sealed class FeedStatus
{
    public string FeedName { get; init; } = "";
    public string Interval { get; init; } = "";
    public long? FirstTimestamp { get; init; }
    public long? LastTimestamp { get; init; }
    public DateTimeOffset? LastRunUtc { get; init; }
    public long RecordCount { get; init; }
    public List<DataGap> Gaps { get; init; } = [];
    public CollectionHealth Health { get; init; } = CollectionHealth.Healthy;
}
