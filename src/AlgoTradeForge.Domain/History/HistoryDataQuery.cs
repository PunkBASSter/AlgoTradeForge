namespace AlgoTradeForge.Domain.History;

public record HistoryDataQuery
{
    public required Asset Asset { get; init; }
    public required TimeSpan TimeFrame { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public int? LastN { get; init; }
    public int? FromIndex { get; init; }
    public int? ToIndex { get; init; }
}

