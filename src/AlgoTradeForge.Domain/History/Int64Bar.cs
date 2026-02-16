namespace AlgoTradeForge.Domain.History;

public readonly record struct Int64Bar(
    long TimestampMs,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}
