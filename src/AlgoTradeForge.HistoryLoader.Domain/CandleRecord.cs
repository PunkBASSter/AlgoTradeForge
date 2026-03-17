namespace AlgoTradeForge.HistoryLoader.Domain;

public readonly record struct CandleRecord(
    long TimestampMs,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume)
{
    public double[]? ExtValues { get; init; }
}
