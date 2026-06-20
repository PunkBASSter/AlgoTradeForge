namespace AlgoTradeForge.Domain.History;

public readonly record struct TradeTick(
    long TimestampMs,
    long Price,
    long Quantity,
    long Sequence,
    AggressorSide Aggressor)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}
